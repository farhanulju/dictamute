// DictaMute: mutes Microsoft Teams (only Teams, by pressing its own mic button) while
// Wispr Flow is dictating, so call participants don't hear your dictation.
//
// - Mouse button 5 (XButton2) arms it. The click is passed through untouched for Wispr.
// - If you're in a Teams call and live, Teams' mic button is pressed via UI Automation
//   (no keystrokes, so nothing interrupts Wispr and nothing beeps).
// - It stays muted while Wispr's microphone stream is active, and unmutes when it stops.
//   If Wispr never starts recording after the click, it unmutes after a moment.
// - Works with Wispr in click (toggle) or press-and-hold mode.
// - Not in a call, already muted, or you unmute yourself: it leaves Teams alone.
//
// Build: build.cmd (or install.cmd) in the repository root.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("DictaMute")]
[assembly: System.Reflection.AssemblyProduct("DictaMute")]
[assembly: System.Reflection.AssemblyDescription("Mutes only Microsoft Teams while Wispr Flow dictates")]
[assembly: System.Reflection.AssemblyCopyright("MIT License")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]

static class Program
{
    [STAThread]
    static void Main()
    {
        bool created;
        using (var mutex = new Mutex(true, "DictaMute.SingleInstance", out created))
        {
            if (!created) return;
            Application.EnableVisualStyles();
            Application.Run(new DictaMuteApp());
        }
    }
}

class DictaMuteApp : ApplicationContext
{
    const int WH_MOUSE_LL = 14;
    const int WM_XBUTTONDOWN = 0x020B;
    const int XBUTTON2 = 2; // "mouse button 5"

    static readonly TimeSpan ArmTimeout = TimeSpan.FromMilliseconds(2500);  // click -> Wispr must start
    static readonly TimeSpan StopDebounce = TimeSpan.FromMilliseconds(600); // Wispr idle this long = stopped

    delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct MSLLHOOKSTRUCT { public int x, y; public uint mouseData, flags, time; public IntPtr extraInfo; }

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")]
    static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string name);

    enum State { Idle, Arming, Recording }

    readonly string dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DictaMute");
    string LogPath { get { return Path.Combine(dir, "log.txt"); } }

    readonly HookProc hookProc;
    readonly IntPtr hook;
    readonly NotifyIcon tray;
    readonly ToolStripMenuItem statusItem;
    readonly BlockingCollection<DateTime> presses = new BlockingCollection<DateTime>();
    volatile string status = "Starting";
    volatile bool stopping;
    readonly Thread worker;

    public DictaMuteApp()
    {
        Directory.CreateDirectory(dir);

        statusItem = new ToolStripMenuItem(status) { Enabled = false };
        var menu = new ContextMenuStrip();
        menu.Items.Add(statusItem);
        menu.Items.Add(new ToolStripSeparator());
        var autostart = new ToolStripMenuItem("Start with Windows") { Checked = Autostart.IsOn(), CheckOnClick = true };
        autostart.CheckedChanged += delegate { Autostart.Set(autostart.Checked); };
        menu.Items.Add(autostart);
        menu.Items.Add("Open log", null, delegate { if (File.Exists(LogPath)) Process.Start("notepad.exe", "\"" + LogPath + "\""); });
        menu.Items.Add("Exit", null, delegate { Shutdown(); });
        tray = new NotifyIcon { Icon = SystemIcons.Shield, Visible = true, ContextMenuStrip = menu, Text = "DictaMute" };

        // First run (e.g. the exe was just downloaded): say where the app went.
        string firstRunMarker = Path.Combine(dir, "first-run-done");
        if (!File.Exists(firstRunMarker))
        {
            tray.ShowBalloonTip(8000, "DictaMute is running",
                "It lives in the system tray. Right-click the shield icon for \"Start with Windows\" and Exit.",
                ToolTipIcon.Info);
            try { File.WriteAllText(firstRunMarker, DateTime.Now.ToString("o")); } catch { }
        }

        var uiTimer = new System.Windows.Forms.Timer { Interval = 500 };
        uiTimer.Tick += delegate
        {
            string s = status;
            statusItem.Text = s;
            string t = "DictaMute: " + s;
            tray.Text = t.Length > 63 ? t.Substring(0, 63) : t;
        };
        uiTimer.Start();

        hookProc = HookCallback;
        hook = SetWindowsHookEx(WH_MOUSE_LL, hookProc, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero) Log("SetWindowsHookEx failed: " + Marshal.GetLastWin32Error());

        // All audio/UIA work happens off the UI thread so the mouse hook never waits on it.
        worker = new Thread(WorkerLoop) { IsBackground = true, Name = "DictaMute worker" };
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
        Log("started");
    }

    IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam.ToInt32() == WM_XBUTTONDOWN)
        {
            var data = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
            if (((data.mouseData >> 16) & 0xFFFF) == XBUTTON2) presses.Add(DateTime.UtcNow);
        }
        // Never swallow the click: Wispr Flow needs it.
        return CallNextHookEx(hook, nCode, wParam, lParam);
    }

    // ---- worker ---------------------------------------------------------------------------

    State state = State.Idle;
    bool ownMute;              // we pressed Teams' button to mute and owe an unmute
    bool muteConfirmed;        // Teams' button has since shown "Unmute" (its label lags the press)
    DateTime armDeadline, lastWisprActive, pressedAt;
    readonly Dictionary<uint, string> processNames = new Dictionary<uint, string>();

    void WorkerLoop()
    {
        var teams = new TeamsMic(Log);
        var lastTeamsScan = DateTime.MinValue;
        while (!stopping)
        {
            DateTime press;
            int clicks = presses.TryTake(out press, 100) ? 1 : 0; // ~10 Hz poll, wakes early on a click
            try
            {
                bool wisprActive = false, teamsCapturing = false;
                foreach (var pid in AudioCapture.ActivePids())
                {
                    string name = ProcessName(pid);
                    if (name.StartsWith("Wispr Flow", StringComparison.OrdinalIgnoreCase)) wisprActive = true;
                    else if (name.Equals("ms-teams", StringComparison.OrdinalIgnoreCase)) teamsCapturing = true;
                }

                // Teams only holds the mic open during a call; skip UIA scans outside calls.
                if (!teamsCapturing) teams.Forget();
                else if (!teams.HasButton && DateTime.UtcNow - lastTeamsScan > TimeSpan.FromSeconds(3))
                {
                    lastTeamsScan = DateTime.UtcNow;
                    teams.Find();
                }

                while (presses.TryTake(out press)) clicks++;
                if (clicks > 0) OnPress(teams, wisprActive, teamsCapturing);
                Tick(teams, wisprActive);

                status = !teamsCapturing ? "Not in a call"
                       : state == State.Idle ? "In a call"
                       : ownMute ? "Muted Teams for dictation" : "Dictating";
            }
            catch (Exception ex) { Log("worker error: " + ex.Message); }
        }
    }

    // The click only arms. Teams is muted once Wispr has actually started recording, so
    // nothing touches Teams while Wispr is still starting up its dictation.
    void OnPress(TeamsMic teams, bool wisprActive, bool inCall)
    {
        if (state != State.Idle) return; // the stop click (or hold release) is seen via Wispr going idle
        if (!inCall) return;
        if (wisprActive) { Log("click: Wispr already using the mic, leaving Teams alone"); return; }
        if (!teams.HasButton) teams.Find();
        bool? muted = teams.IsMuted();
        if (muted != false) { Log("click: Teams " + (muted == true ? "already muted" : "mic button not found")); return; }
        Log("click: armed (focus: " + Focus.Describe(Focus.Current()) + ")");
        state = State.Arming;
        armDeadline = DateTime.UtcNow + ArmTimeout;
    }

    void Tick(TeamsMic teams, bool wisprActive)
    {
        var now = DateTime.UtcNow;
        if (wisprActive) lastWisprActive = now;
        switch (state)
        {
            case State.Arming:
                if (wisprActive)
                {
                    state = State.Recording;
                    if (teams.IsMuted() == false) PressTeams(teams, "Wispr recording -> muted Teams", true);
                    else Log("Wispr recording, Teams already muted");
                }
                else if (now > armDeadline) { Log("Wispr didn't start"); state = State.Idle; }
                break;
            case State.Recording:
                if (!wisprActive && now - lastWisprActive > StopDebounce) { Log("Wispr stopped"); Restore(teams); }
                break;
        }

        if (!ownMute) return;
        bool? muted = teams.IsMuted();
        if (!muteConfirmed)
        {
            if (muted == true) muteConfirmed = true;
            else if (now - pressedAt > TimeSpan.FromSeconds(3)) { ownMute = false; Log("Teams never showed muted; not tracking"); }
        }
        // You unmuted yourself mid-dictation: don't fight it, and don't "restore" later.
        else if (muted == false) { ownMute = false; Log("you unmuted manually"); }
    }

    void Restore(TeamsMic teams)
    {
        state = State.Idle;
        if (!ownMute) return;
        // Our mute may not show yet (very short dictation): give Teams a moment to catch up.
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!muteConfirmed && DateTime.UtcNow < until)
        {
            if (teams.IsMuted() == true) muteConfirmed = true; else Thread.Sleep(50);
        }
        if (teams.IsMuted() == true) PressTeams(teams, "unmuted Teams", false);
        else Log("Teams already live, nothing to undo");
        ownMute = false;
    }

    void PressTeams(TeamsMic teams, string what, bool muting)
    {
        IntPtr before = Focus.Current();
        if (!teams.Press()) return;
        if (muting) { ownMute = true; muteConfirmed = false; pressedAt = DateTime.UtcNow; }
        Log(what);
        // Pressing a Teams button must not pull focus away from where Wispr is typing.
        for (int i = 0; i < 6; i++)
        {
            Thread.Sleep(40);
            IntPtr now = Focus.Current();
            if (now != before && before != IntPtr.Zero)
            {
                Log("focus moved to " + Focus.Describe(now) + "; restoring " + Focus.Describe(before));
                Focus.Restore(before);
                break;
            }
        }
    }

    string ProcessName(uint pid)
    {
        string name;
        if (processNames.TryGetValue(pid, out name)) return name;
        try { name = Process.GetProcessById((int)pid).ProcessName; } catch { name = ""; }
        if (processNames.Count > 200) processNames.Clear();
        processNames[pid] = name;
        return name;
    }

    // ---- misc -----------------------------------------------------------------------------

    readonly object logGate = new object();
    void Log(string line)
    {
        lock (logGate)
        {
            try
            {
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 512 * 1024) File.Delete(LogPath);
                File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff ") + line + Environment.NewLine);
            }
            catch { }
        }
    }

    void Shutdown()
    {
        stopping = true;
        worker.Join(2000);
        if (ownMute)
        {
            var teams = new TeamsMic(Log);
            teams.Find();
            if (teams.IsMuted() == true) teams.Press();
        }
        if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
        tray.Visible = false;
        tray.Dispose();
        ExitThread();
    }
}

/// <summary>"Start with Windows": a per-user Run entry pointing at this exe, wherever it lives.</summary>
static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "DictaMute";

    static string Command { get { return "\"" + Application.ExecutablePath + "\""; } }

    public static bool IsOn()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
        {
            var value = key == null ? null : key.GetValue(ValueName) as string;
            return value != null && string.Equals(value, Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Set(bool on)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (on) key.SetValue(ValueName, Command);
            else key.DeleteValue(ValueName, false);
        }
    }
}

/// <summary>Foreground-window helpers, so pressing Teams' button never steals focus.</summary>
static class Focus
{
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);

    public static IntPtr Current() { return GetForegroundWindow(); }

    public static string Describe(IntPtr h)
    {
        if (h == IntPtr.Zero) return "(none)";
        uint pid; GetWindowThreadProcessId(h, out pid);
        string proc; try { proc = Process.GetProcessById((int)pid).ProcessName; } catch { proc = "?"; }
        var t = new StringBuilder(80); GetWindowText(h, t, 80);
        return proc + " '" + t + "'";
    }

    public static void Restore(IntPtr h)
    {
        uint pid;
        uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out pid);
        uint me = GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
        BringWindowToTop(h);
        SetForegroundWindow(h);
        if (attached) AttachThreadInput(me, fgThread, false);
    }
}

/// <summary>Teams' in-call mic button, found and pressed through UI Automation.</summary>
class TeamsMic
{
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);

    static readonly Condition MicCondition =
        new PropertyCondition(AutomationElement.AutomationIdProperty, "microphone-button");

    readonly Action<string> log;
    AutomationElement button;

    public TeamsMic(Action<string> log) { this.log = log; }

    public bool HasButton { get { return button != null; } }
    public void Forget() { button = null; }

    public void Find()
    {
        button = null;
        var pids = new HashSet<uint>();
        foreach (var p in Process.GetProcessesByName("ms-teams")) pids.Add((uint)p.Id);
        var windows = new List<IntPtr>();
        EnumWindows((h, l) =>
        {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pids.Contains(pid) && IsWindowVisible(h))
            {
                var c = new StringBuilder(64); GetClassName(h, c, 64);
                if (c.ToString() == "TeamsWebView") windows.Add(h);
            }
            return true;
        }, IntPtr.Zero);
        foreach (var h in windows)
        {
            try
            {
                var el = AutomationElement.FromHandle(h).FindFirst(TreeScope.Descendants, MicCondition);
                if (el != null) { button = el; return; }
            }
            catch (Exception) { }
        }
    }

    /// <summary>true = muted, false = live, null = no call / button not found.</summary>
    public bool? IsMuted()
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (button == null) { if (attempt == 0) Find(); continue; }
            try
            {
                string name = button.Current.Name ?? "";
                if (name.StartsWith("Unmute", StringComparison.OrdinalIgnoreCase)) return true;
                if (name.StartsWith("Mute", StringComparison.OrdinalIgnoreCase)) return false;
                log("unrecognised mic button name: '" + name + "'");
                return null;
            }
            catch (ElementNotAvailableException) { Find(); }
        }
        return null;
    }

    public bool Press()
    {
        try
        {
            ((InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            return true;
        }
        catch (Exception ex) { log("pressing the Teams mic button failed: " + ex.Message); return false; }
    }
}
