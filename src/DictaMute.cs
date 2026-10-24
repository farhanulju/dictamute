// DictaMute: mutes your call app (Microsoft Teams or Discord; only the call app, by pressing its
// own mic button) while a dictation app such as Wispr Flow or Claude is listening, so your call
// doesn't hear it.
//
// - Each watched app either starts on a shortcut (keys and/or middle/thumb mouse buttons,
//   always passed through to the app) or whenever it opens the microphone.
// - Once the app is actually listening, the call app's mic button is pressed via UI Automation
//   (no keystrokes, so nothing interrupts the dictation app and nothing beeps). See CallApps.cs.
// - The call is unmuted when the dictation app's microphone stream goes idle.
// - Not in a call, already muted, paused, or you unmute yourself: it leaves the call alone.
//
// Build: build.cmd (or install.cmd) in the repository root.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("DictaMute")]
[assembly: System.Reflection.AssemblyProduct("DictaMute")]
[assembly: System.Reflection.AssemblyDescription("Mutes only your Teams or Discord call while your dictation app is listening")]
[assembly: System.Reflection.AssemblyCopyright("MIT License")]
[assembly: System.Reflection.AssemblyVersion("1.3.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.3.0.0")]

static class Program
{
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    public const string Version = "1.3.0";

    [STAThread]
    static void Main(string[] args)
    {
        SetProcessDPIAware(); // crisp tray icon and settings window on scaled displays
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (args.Length == 2 && args[0] == "--screenshots") { Screenshots.Capture(args[1]); return; }

        bool created;
        using (var mutex = new Mutex(true, "DictaMute.SingleInstance", out created))
        {
            if (!created) return;
            Application.Run(new DictaMuteApp());
        }
    }
}

/// <summary>
/// Low-level keyboard + mouse hooks. Input is only observed (and always passed on), except
/// while a ShortcutBox is recording a new shortcut.
/// </summary>
static class Hooks
{
    const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    const int WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208, WM_XBUTTONDOWN = 0x020B, WM_XBUTTONUP = 0x020C;

    delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extraInfo; }
    [StructLayout(LayoutKind.Sequential)] struct MSLLHOOKSTRUCT { public int x, y; public uint mouseData, flags, time; public IntPtr extraInfo; }
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);

    static HookProc keyboardProc, mouseProc; // kept alive for the hooks' lifetime
    static IntPtr keyboardHook, mouseHook;
    static readonly HashSet<int> down = new HashSet<int>();

    /// <summary>Called with (virtual key, isDown) for every key/button event. Runs on the UI thread.</summary>
    public static Action<int, bool> Observer;
    /// <summary>Set by a ShortcutBox while recording; return true to swallow the input.</summary>
    public static Func<int, bool, bool> Recorder;

    public static IEnumerable<int> Down { get { return down; } }

    public static void Install()
    {
        keyboardProc = OnKeyboard;
        mouseProc = OnMouse;
        IntPtr module = GetModuleHandle(null);
        keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, keyboardProc, module, 0);
        mouseHook = SetWindowsHookEx(WH_MOUSE_LL, mouseProc, module, 0);
    }

    public static void Uninstall()
    {
        if (keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(keyboardHook);
        if (mouseHook != IntPtr.Zero) UnhookWindowsHookEx(mouseHook);
    }

    static IntPtr OnKeyboard(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            if (isDown || msg == WM_KEYUP || msg == WM_SYSKEYUP)
            {
                var data = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                if (Handle(Shortcut.Canonical((int)data.vkCode), isDown)) return (IntPtr)1;
            }
        }
        return CallNextHookEx(keyboardHook, nCode, wParam, lParam);
    }

    static IntPtr OnMouse(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            int vk = 0; bool isDown = false;
            if (msg == WM_MBUTTONDOWN || msg == WM_MBUTTONUP) { vk = Shortcut.Mouse3; isDown = msg == WM_MBUTTONDOWN; }
            else if (msg == WM_XBUTTONDOWN || msg == WM_XBUTTONUP)
            {
                var data = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                vk = ((data.mouseData >> 16) & 0xFFFF) == 2 ? Shortcut.Mouse5 : Shortcut.Mouse4;
                isDown = msg == WM_XBUTTONDOWN;
            }
            if (vk != 0 && Handle(vk, isDown)) return (IntPtr)1;
        }
        return CallNextHookEx(mouseHook, nCode, wParam, lParam);
    }

    static bool Handle(int vk, bool isDown)
    {
        var recorder = Recorder;
        if (recorder != null) { down.Clear(); return recorder(vk, isDown); }
        if (isDown) { if (!down.Add(vk)) return false; } // ignore key auto-repeat
        else down.Remove(vk);
        var observer = Observer;
        if (observer != null) observer(vk, isDown);
        return false;
    }
}

class DictaMuteApp : ApplicationContext
{
    enum State { Idle, Arming, Listening }

    /// <summary>Runtime state for one watched app.</summary>
    class Watch
    {
        public AppRule Rule;
        public List<HashSet<int>> Combos;
        public State State = State.Idle;
        public bool Active, WasActive;
        public DateTime ArmDeadline, LastActive;
    }

    static readonly TimeSpan ArmTimeout = TimeSpan.FromMilliseconds(2500); // shortcut -> app must start listening

    string LogPath { get { return Path.Combine(Settings.Dir, "log.txt"); } }

    readonly NotifyIcon tray;
    readonly ContextMenuStrip menu;
    readonly ToolStripMenuItem statusItem, pauseItem;
    readonly Icon[] trayIcons = new Icon[3]; // Idle, InCall, Muting
    readonly ConcurrentQueue<AppRule> arms = new ConcurrentQueue<AppRule>();
    readonly AutoResetEvent wake = new AutoResetEvent(false);
    readonly Thread worker;

    volatile Settings settings;
    volatile List<Watch> watches;  // rebuilt whenever settings change
    volatile bool paused, stopping;
    volatile string status = "Starting";
    volatile int iconState = (int)IconState.Idle;
    SettingsForm settingsForm;

    public DictaMuteApp()
    {
        Directory.CreateDirectory(Settings.Dir);
        bool firstRun = !Settings.Exists;
        settings = Settings.Load();
        if (firstRun) settings.Save();
        watches = BuildWatches(settings);

        menu = BuildMenu(this, out statusItem, out pauseItem);
        int iconSize = SystemInformation.SmallIconSize.Width; // 16 at 100% scaling, 32 at 200%
        for (int i = 0; i < trayIcons.Length; i++) trayIcons[i] = Icons.Tray((IconState)i, iconSize);
        tray = new NotifyIcon { Icon = trayIcons[0], Visible = true, ContextMenuStrip = menu, Text = "DictaMute" };
        tray.DoubleClick += delegate { OpenSettings(); };

        // Fast enough that the red "muting" icon appears with the mute.
        var uiTimer = new System.Windows.Forms.Timer { Interval = 150 };
        uiTimer.Tick += delegate
        {
            string s = status;
            statusItem.Text = s;
            string t = "DictaMute: " + s;
            tray.Text = t.Length > 63 ? t.Substring(0, 63) : t;
            var icon = trayIcons[iconState];
            if (tray.Icon != icon) tray.Icon = icon;
        };
        uiTimer.Start();

        Hooks.Observer = OnInput;
        Hooks.Install();

        // All audio/UIA work happens off the UI thread so the hooks never wait on it.
        calls = CallMic.All(Log);
        worker = new Thread(WorkerLoop) { IsBackground = true, Name = "DictaMute worker" };
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
        Log("started " + Program.Version + ": " + string.Join("; ", settings.Apps.Select(a => a.Name + " [" + (a.Enabled ? Shortcut.Describe(a.Shortcut) : "off") + "]")));

        if (firstRun)
        {
            tray.ShowBalloonTip(8000, "DictaMute is running",
                "It lives in the system tray. Double-click the microphone icon for settings.", ToolTipIcon.Info);
            var once = new System.Windows.Forms.Timer { Interval = 1500 };
            once.Tick += delegate { once.Stop(); OpenSettings(); };
            once.Start();
        }
    }

    /// <summary>The tray menu. With app == null it's inert (used for README screenshots).</summary>
    public static ContextMenuStrip BuildMenu(DictaMuteApp app, out ToolStripMenuItem status, out ToolStripMenuItem pause)
    {
        var menu = new ContextMenuStrip { Font = Ui.Font };
        status = new ToolStripMenuItem("Starting") { Enabled = false };
        menu.Items.Add(status);
        menu.Items.Add(new ToolStripSeparator());
        var settingsItem = new ToolStripMenuItem("Settings…") { Font = new Font(Ui.Font, FontStyle.Bold) };
        menu.Items.Add(settingsItem);
        pause = new ToolStripMenuItem("Pause") { CheckOnClick = true };
        menu.Items.Add(pause);
        var autostart = new ToolStripMenuItem("Start with Windows") { Checked = Autostart.IsOn(), CheckOnClick = true };
        menu.Items.Add(autostart);
        var log = new ToolStripMenuItem("Open log");
        menu.Items.Add(log);
        menu.Items.Add(new ToolStripSeparator());
        var exit = new ToolStripMenuItem("Exit");
        menu.Items.Add(exit);
        if (app != null)
        {
            var p = pause;
            settingsItem.Click += delegate { app.OpenSettings(); };
            pause.CheckedChanged += delegate { app.SetPaused(p.Checked); };
            autostart.CheckedChanged += delegate { Autostart.Set(autostart.Checked); };
            menu.Opening += delegate { autostart.Checked = Autostart.IsOn(); };
            log.Click += delegate { if (File.Exists(app.LogPath)) Process.Start("notepad.exe", "\"" + app.LogPath + "\""); };
            exit.Click += delegate { app.Shutdown(); };
        }
        return menu;
    }

    void OpenSettings()
    {
        if (settingsForm != null) { settingsForm.Activate(); return; }
        using (settingsForm = new SettingsForm(settings, LogPath, Program.Version))
        {
            if (settingsForm.ShowDialog() == DialogResult.OK && settingsForm.Result != null)
            {
                settings = settingsForm.Result;
                watches = BuildWatches(settings);
                wake.Set();
                Log("settings saved: " + string.Join("; ", settings.Apps.Select(a => a.Name + " [" + (a.Enabled ? Shortcut.Describe(a.Shortcut) : "off") + "]")));
            }
        }
        settingsForm = null;
    }

    void SetPaused(bool value)
    {
        paused = value;
        Log(value ? "paused" : "resumed");
        wake.Set();
    }

    static List<Watch> BuildWatches(Settings s)
    {
        return s.Apps.Where(a => a.Enabled && a.Process.Length > 0)
            .Select(a => new Watch { Rule = a, Combos = Shortcut.Parse(a.Shortcut) })
            .ToList();
    }

    // UI thread (from the hooks): a key/button went down. If it completes an app's shortcut, arm it.
    void OnInput(int vk, bool isDown)
    {
        if (!isDown) return;
        var current = new HashSet<int>(Hooks.Down);
        foreach (var w in watches)
            foreach (var combo in w.Combos)
                if (combo.Contains(vk) && combo.IsSubsetOf(current)) { arms.Enqueue(w.Rule); wake.Set(); break; }
    }

    // ---- worker ---------------------------------------------------------------------------

    readonly List<CallMic> calls;   // Teams, Discord; their tracking state belongs to the worker thread
    readonly Dictionary<uint, string> processNames = new Dictionary<uint, string>();

    IEnumerable<CallMic> EnabledCalls(Settings s)
    {
        return calls.Where(c => s.Mutes(c.Name));
    }

    void WorkerLoop()
    {
        while (!stopping)
        {
            wake.WaitOne(100); // ~10 Hz poll, wakes early on a shortcut
            try
            {
                var list = watches;
                var s = settings;

                var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var pid in AudioCapture.ActivePids())
                {
                    string name = ProcessName(pid);
                    if (name.Length > 0) active.Add(name);
                }
                // Call apps hold the mic only during a call, so "has the mic open" = "in a call".
                var enabled = EnabledCalls(s).ToList();
                foreach (var c in calls)
                {
                    c.InCall = enabled.Contains(c) && active.Contains(c.Process);
                    if (!c.InCall) { c.Forget(); c.OwnMute = false; }
                    else if (!c.HasButton && DateTime.UtcNow - c.LastScan > TimeSpan.FromSeconds(3))
                    {
                        c.LastScan = DateTime.UtcNow;
                        c.Find();
                    }
                }
                bool inCall = calls.Any(c => c.InCall);
                foreach (var w in list) w.Active = active.Any(w.Rule.Matches);

                AppRule armed;
                while (arms.TryDequeue(out armed)) OnArm(list, armed, inCall);
                Tick(list, inCall);

                var listening = list.FirstOrDefault(w => w.State == State.Listening);
                var muted = calls.Where(c => c.OwnMute).Select(c => c.Name).ToList();
                status = paused ? "Paused"
                       : !inCall ? "Not in a call"
                       : muted.Count > 0 && listening != null ? "Muted " + string.Join(" + ", muted) + " for " + listening.Rule.Name
                       : "In a " + string.Join(" + ", calls.Where(c => c.InCall).Select(c => c.Name)) + " call";
                iconState = (int)(paused || !inCall ? IconState.Idle : muted.Count > 0 ? IconState.Muting : IconState.InCall);
            }
            catch (Exception ex) { Log("worker error: " + ex.Message); }
        }
    }

    // A shortcut only arms the app. Calls are muted once the app is actually listening, so
    // nothing touches the call while the app is still starting up its dictation.
    void OnArm(List<Watch> list, AppRule rule, bool inCall)
    {
        var w = list.FirstOrDefault(x => x.Rule == rule);
        if (w == null || w.State != State.Idle || paused || !inCall) return; // a stop press is seen via the mic going idle
        if (w.Active) { Log(rule.Name + ": shortcut, but it's already using the mic; leaving the call alone"); return; }
        Log(rule.Name + ": shortcut, armed (focus: " + Focus.Describe(Focus.Current()) + ")");
        w.State = State.Arming;
        w.ArmDeadline = DateTime.UtcNow + ArmTimeout;
    }

    void Tick(List<Watch> list, bool inCall)
    {
        var now = DateTime.UtcNow;
        var delay = TimeSpan.FromMilliseconds(settings.UnmuteDelayMs);
        bool anyWasListening = list.Any(w => w.State == State.Listening);

        foreach (var w in list)
        {
            if (w.Active) w.LastActive = now;
            if (!inCall || paused) w.State = State.Idle;
            else switch (w.State)
            {
                case State.Idle:
                    // "Whenever it uses the mic" apps start on the idle -> active edge.
                    if (w.Combos.Count == 0 && w.Active && !w.WasActive) { w.State = State.Listening; Log(w.Rule.Name + ": started listening"); }
                    break;
                case State.Arming:
                    if (w.Active) { w.State = State.Listening; Log(w.Rule.Name + ": started listening"); }
                    else if (now > w.ArmDeadline) { w.State = State.Idle; Log(w.Rule.Name + ": didn't start listening"); }
                    break;
                case State.Listening:
                    if (!w.Active && now - w.LastActive > delay) { w.State = State.Idle; Log(w.Rule.Name + ": stopped listening"); }
                    break;
            }
            w.WasActive = w.Active;
        }

        bool anyListening = list.Any(w => w.State == State.Listening);
        foreach (var c in calls.Where(c => c.InCall))
        {
            if (anyListening && !anyWasListening)
            {
                if (c.IsMuted() == false) Press(c, "muted " + c.Name, true);
                else Log(c.Name + " already muted");
            }
            else if (!anyListening && anyWasListening) Restore(c);

            if (!c.OwnMute) continue;
            bool? muted = c.IsMuted();
            if (!c.MuteConfirmed)
            {
                if (muted == true) c.MuteConfirmed = true;
                else if (now - c.PressedAt > TimeSpan.FromSeconds(3)) { c.OwnMute = false; Log(c.Name + " never showed muted; not tracking"); }
            }
            // You unmuted yourself mid-dictation: don't fight it, and don't "restore" later.
            else if (muted == false) { c.OwnMute = false; Log("you unmuted " + c.Name + " manually"); }
        }
    }

    void Restore(CallMic c)
    {
        if (!c.OwnMute) return;
        // Our mute may not show yet (very short dictation): give the app a moment to catch up.
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!c.MuteConfirmed && DateTime.UtcNow < until)
        {
            if (c.IsMuted() == true) c.MuteConfirmed = true; else Thread.Sleep(50);
        }
        if (c.IsMuted() == true) Press(c, "unmuted " + c.Name, false);
        else Log(c.Name + " already live, nothing to undo");
        c.OwnMute = false;
    }

    void Press(CallMic c, string what, bool muting)
    {
        IntPtr before = Focus.Current();
        if (!c.Press()) return;
        if (muting) { c.OwnMute = true; c.MuteConfirmed = false; c.PressedAt = DateTime.UtcNow; }
        Log(what);
        // Pressing a call app's button must not pull focus away from where you're dictating.
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
        wake.Set();
        worker.Join(2000);
        foreach (var c in calls.Where(c => c.OwnMute))
            if (c.IsMuted() == true) c.Press(); // don't leave a call muted on Exit
        Hooks.Uninstall();
        tray.Visible = false;
        tray.Dispose();
        ExitThread();
    }
}

/// <summary>DictaMute.exe --screenshots &lt;dir&gt;: renders the README images from the real UI.</summary>
static class Screenshots
{
    public static void Capture(string dir)
    {
        Directory.CreateDirectory(dir);
        var demo = new Settings();
        demo.Apps.Add(new AppRule { Name = "Wispr Flow", Process = "Wispr Flow", Shortcut = "Mouse5" });
        demo.Apps.Add(new AppRule { Name = "Claude", Process = "claude", Shortcut = "" });

        using (var form = new SettingsForm(demo, "", Program.Version) { StartPosition = FormStartPosition.Manual, Location = new Point(80, 80), TopMost = true })
        {
            form.Show();
            form.SelectFirst();
            Grab(form, Path.Combine(dir, "settings.png"));
            using (var dlg = new AppDialog(demo.Apps[1]) { StartPosition = FormStartPosition.Manual, Location = new Point(80, 80), TopMost = true })
            {
                dlg.Show();
                Grab(dlg, Path.Combine(dir, "edit-app.png"));
            }
            using (var dlg = new AppDialog(demo.Apps[0]) { StartPosition = FormStartPosition.Manual, Location = new Point(80, 80), TopMost = true })
            {
                dlg.Show();
                Grab(dlg, Path.Combine(dir, "edit-app-shortcut.png"));
            }
        }

        ToolStripMenuItem status, pause;
        using (var menu = DictaMuteApp.BuildMenu(null, out status, out pause))
        {
            status.Text = "Muted Teams for Wispr Flow";
            menu.Show(new Point(80, 80));
            Pump(300);
            using (var bmp = new Bitmap(menu.Width, menu.Height))
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(menu.Bounds.Location, Point.Empty, menu.Size);
                bmp.Save(Path.Combine(dir, "tray-menu.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            menu.Close();
        }
    }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);

    static void Grab(Form form, string path)
    {
        form.Activate();
        Pump(400);
        // The visible frame, without Windows 10/11's invisible resize borders.
        RECT f;
        var r = DwmGetWindowAttribute(form.Handle, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out f, Marshal.SizeOf(typeof(RECT))) == 0
            ? Rectangle.FromLTRB(f.Left, f.Top, f.Right, f.Bottom)
            : form.Bounds;
        using (var bmp = new Bitmap(r.Width, r.Height))
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(r.Location, Point.Empty, r.Size);
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
    }

    static void Pump(int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(15); }
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

