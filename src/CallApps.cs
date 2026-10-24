// Call apps DictaMute can mute: each one finds its own in-call mic button through UI Automation,
// reads whether it's muted, and presses it. Add a new app by subclassing CallMic.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

abstract class CallMic
{
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);

    protected readonly Action<string> Log;
    protected AutomationElement Button;

    // Tracking, owned by the worker thread.
    public bool InCall;          // the app has the microphone open (both apps only do that in a call)
    public bool OwnMute;         // we pressed its button to mute and owe an unmute
    public bool MuteConfirmed;   // the button has since shown "muted" (its state lags the press)
    public DateTime PressedAt, LastScan;

    protected CallMic(Action<string> log) { Log = log; }

    /// <summary>Settings key and the name used in the log and tray ("Teams", "Discord").</summary>
    public abstract string Name { get; }
    /// <summary>Process name without .exe.</summary>
    public abstract string Process { get; }
    protected abstract string WindowClass { get; }
    protected abstract AutomationElement FindButton(AutomationElement window);
    /// <summary>true = muted, false = live, null = can't tell.</summary>
    protected abstract bool? ReadMuted(AutomationElement button);

    public bool HasButton { get { return Button != null; } }
    public void Forget() { Button = null; }

    public void Find()
    {
        Button = null;
        var pids = new HashSet<uint>();
        foreach (var p in System.Diagnostics.Process.GetProcessesByName(Process)) pids.Add((uint)p.Id);
        var windows = new List<IntPtr>();
        EnumWindows((h, l) =>
        {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pids.Contains(pid) && IsWindowVisible(h))
            {
                var c = new StringBuilder(64); GetClassName(h, c, 64);
                if (c.ToString() == WindowClass) windows.Add(h);
            }
            return true;
        }, IntPtr.Zero);
        foreach (var h in windows)
        {
            try
            {
                var el = FindButton(AutomationElement.FromHandle(h));
                if (el != null) { Button = el; return; }
            }
            catch (Exception) { }
        }
    }

    public bool? IsMuted()
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (Button == null) { if (attempt == 0) Find(); continue; }
            try { return ReadMuted(Button); }
            catch (ElementNotAvailableException) { Find(); }
        }
        return null;
    }

    /// <summary>Invoke for plain buttons, Toggle for switch-style buttons.</summary>
    public bool Press()
    {
        try
        {
            object pattern;
            if (Button.TryGetCurrentPattern(InvokePattern.Pattern, out pattern)) ((InvokePattern)pattern).Invoke();
            else ((TogglePattern)Button.GetCurrentPattern(TogglePattern.Pattern)).Toggle();
            return true;
        }
        catch (Exception ex) { Log("pressing the " + Name + " mic button failed: " + ex.Message); return false; }
    }

    public static List<CallMic> All(Action<string> log)
    {
        return new List<CallMic> { new TeamsMic(log), new DiscordMic(log) };
    }

    /// <summary>Name -> label, for the settings window.</summary>
    public static readonly KeyValuePair<string, string>[] Known =
    {
        new KeyValuePair<string, string>("Teams", "Microsoft Teams"),
        new KeyValuePair<string, string>("Discord", "Discord"),
    };
}

/// <summary>New Microsoft Teams: the meeting window's "Mute mic" / "Unmute mic" button.</summary>
class TeamsMic : CallMic
{
    static readonly Condition MicCondition =
        new PropertyCondition(AutomationElement.AutomationIdProperty, "microphone-button");

    public TeamsMic(Action<string> log) : base(log) { }
    public override string Name { get { return "Teams"; } }
    public override string Process { get { return "ms-teams"; } }
    protected override string WindowClass { get { return "TeamsWebView"; } }

    protected override AutomationElement FindButton(AutomationElement window)
    {
        return window.FindFirst(TreeScope.Descendants, MicCondition);
    }

    protected override bool? ReadMuted(AutomationElement button)
    {
        string name = button.Current.Name ?? "";
        if (name.StartsWith("Unmute", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.StartsWith("Mute", StringComparison.OrdinalIgnoreCase)) return false;
        Log("unrecognised Teams mic button name: '" + name + "'");
        return null;
    }
}

/// <summary>
/// Discord desktop: the "Mute" switch in the user panel (bottom left). Its label stays "Mute";
/// the switch's on/off state is the mute state.
/// </summary>
class DiscordMic : CallMic
{
    static readonly Condition MuteCondition = new AndCondition(
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
        new OrCondition(
            new PropertyCondition(AutomationElement.NameProperty, "Mute"),
            new PropertyCondition(AutomationElement.NameProperty, "Unmute")));

    public DiscordMic(Action<string> log) : base(log) { }
    public override string Name { get { return "Discord"; } }
    public override string Process { get { return "Discord"; } }
    protected override string WindowClass { get { return "Chrome_WidgetWin_1"; } }

    protected override AutomationElement FindButton(AutomationElement window)
    {
        AutomationElement fallback = null;
        foreach (AutomationElement el in window.FindAll(TreeScope.Descendants, MuteCondition))
        {
            object pattern;
            if (el.TryGetCurrentPattern(TogglePattern.Pattern, out pattern)) return el; // the user-panel switch
            if (fallback == null) fallback = el;
        }
        return fallback;
    }

    protected override bool? ReadMuted(AutomationElement button)
    {
        object pattern;
        if (button.TryGetCurrentPattern(TogglePattern.Pattern, out pattern))
        {
            var state = ((TogglePattern)pattern).Current.ToggleState;
            if (state == ToggleState.On) return true;
            if (state == ToggleState.Off) return false;
        }
        string name = button.Current.Name ?? "";
        if (name.Equals("Unmute", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("Mute", StringComparison.OrdinalIgnoreCase)) return false;
        Log("unrecognised Discord mute button: '" + name + "'");
        return null;
    }
}
