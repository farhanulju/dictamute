// Settings UI: the list of dictation apps, the per-app editor, and the shortcut recorder.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

static class Ui
{
    public static readonly Font Font = new Font("Segoe UI", 9f);
    public static readonly Font Heading = new Font("Segoe UI Semibold", 10.5f);
    public static readonly Color Muted = Color.FromArgb(0x5F, 0x63, 0x68);

    public static Label Text(string text, Font font = null, Color? color = null)
    {
        return new Label
        {
            Text = text, AutoSize = true, Font = font ?? Font, ForeColor = color ?? SystemColors.ControlText,
            Margin = new Padding(0, 0, 0, 6), MaximumSize = new Size(520, 0),
        };
    }

    public static Button Button(string text, EventHandler onClick)
    {
        var b = new Button { Text = text, AutoSize = true, MinimumSize = new Size(88, 28), Margin = new Padding(0, 0, 8, 0), Font = Font };
        b.Click += onClick;
        return b;
    }

    public static TableLayoutPanel Column()
    {
        return new TableLayoutPanel
        {
            ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill, Padding = new Padding(16), Font = Font,
        };
    }

    public static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 8), Font = Font };
        row.Controls.AddRange(controls);
        return row;
    }
}

/// <summary>Main settings window.</summary>
class SettingsForm : Form
{
    readonly Settings working;
    readonly ListView list;
    readonly NumericUpDown delay;
    readonly CheckBox autostart;
    readonly List<CheckBox> callBoxes = new List<CheckBox>();
    public Settings Result { get; private set; }

    public SettingsForm(Settings current, string logPath, string version)
    {
        working = current.Clone();
        Text = "DictaMute settings";
        Font = Ui.Font;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        AutoScaleMode = AutoScaleMode.Font;
        Icon = Icons.Tray(IconState.App, 32);

        var col = Ui.Column();
        col.Controls.Add(Ui.Text("Mute your call while these apps are listening", Ui.Heading));
        col.Controls.Add(Ui.Text(
            "During a Teams or Discord call, DictaMute mutes you in the call while one of these apps is " +
            "taking dictation, then unmutes you when the app stops. Your microphone itself is never muted.", null, Ui.Muted));

        list = new ListView
        {
            View = View.Details, CheckBoxes = true, FullRowSelect = true, HideSelection = false,
            HeaderStyle = ColumnHeaderStyle.Nonclickable, Size = new Size(520, 140), Margin = new Padding(0, 4, 0, 8),
        };
        list.Columns.Add("App", 150);
        list.Columns.Add("Starts muting the call", 345);
        list.DoubleClick += delegate { EditSelected(); };
        list.ItemChecked += delegate(object s, ItemCheckedEventArgs e) { ((AppRule)e.Item.Tag).Enabled = e.Item.Checked; };
        col.Controls.Add(list);

        col.Controls.Add(Ui.Row(
            Ui.Button("Add app…", delegate { AddApp(); }),
            Ui.Button("Edit…", delegate { EditSelected(); }),
            Ui.Button("Remove", delegate { RemoveSelected(); })));

        col.Controls.Add(Ui.Text("Options", Ui.Heading));
        var callsLabel = Ui.Text("Mute me in");
        callsLabel.Margin = new Padding(0, 4, 6, 0);
        var callsRow = Ui.Row(callsLabel);
        foreach (var known in CallMic.Known)
        {
            var box = new CheckBox { Text = known.Value, Tag = known.Key, AutoSize = true, Checked = working.Mutes(known.Key), Margin = new Padding(0, 3, 12, 0) };
            callBoxes.Add(box);
            callsRow.Controls.Add(box);
        }
        col.Controls.Add(callsRow);
        delay = new NumericUpDown { Minimum = 100, Maximum = 10000, Increment = 100, Value = working.UnmuteDelayMs, Width = 70, Margin = new Padding(0, 0, 6, 0) };
        var delayRow = Ui.Row(Ui.Text("Unmute the call"), delay, Ui.Text("ms after the app stops listening"));
        foreach (Control c in delayRow.Controls) if (c is Label) c.Margin = new Padding(0, 4, 6, 0);
        col.Controls.Add(delayRow);
        autostart = new CheckBox { Text = "Start DictaMute with Windows", AutoSize = true, Checked = Autostart.IsOn(), Margin = new Padding(0, 0, 0, 12) };
        col.Controls.Add(autostart);

        var log = new LinkLabel { Text = "Open log", AutoSize = true, Margin = new Padding(0, 6, 16, 0) };
        log.LinkClicked += delegate { if (File.Exists(logPath)) Process.Start("notepad.exe", "\"" + logPath + "\""); };
        var ver = Ui.Text("DictaMute " + version, null, Ui.Muted);
        ver.Margin = new Padding(0, 6, 24, 0);
        var save = Ui.Button("Save", delegate { SaveAndClose(); });
        var cancel = Ui.Button("Cancel", delegate { Close(); });
        cancel.Margin = new Padding(0);
        col.Controls.Add(Ui.Row(log, ver, save, cancel));
        AcceptButton = save; CancelButton = cancel;

        Controls.Add(col);
        Reload();
    }

    void Reload()
    {
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var app in working.Apps)
        {
            var item = new ListViewItem(app.Name) { Tag = app, Checked = app.Enabled };
            item.SubItems.Add(Shortcut.Describe(app.Shortcut));
            list.Items.Add(item);
        }
        list.EndUpdate();
    }

    AppRule Selected { get { return list.SelectedItems.Count > 0 ? (AppRule)list.SelectedItems[0].Tag : null; } }

    void AddApp()
    {
        using (var dlg = new AppDialog(new AppRule()))
            if (dlg.ShowDialog(this) == DialogResult.OK) { working.Apps.Add(dlg.Result); Reload(); }
    }

    void EditSelected()
    {
        var app = Selected;
        if (app == null) return;
        using (var dlg = new AppDialog(app))
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                working.Apps[working.Apps.IndexOf(app)] = dlg.Result;
                Reload();
            }
    }

    void RemoveSelected()
    {
        var app = Selected;
        if (app != null) { working.Apps.Remove(app); Reload(); }
    }

    void SaveAndClose()
    {
        working.UnmuteDelayMs = (int)delay.Value;
        working.CallApps = callBoxes.Where(b => b.Checked).Select(b => (string)b.Tag).ToList();
        if (autostart.Checked != Autostart.IsOn()) Autostart.Set(autostart.Checked);
        working.Save();
        Result = working;
        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>For README screenshots: the list with one row selected.</summary>
    public void SelectFirst() { if (list.Items.Count > 0) list.Items[0].Selected = true; }
}

/// <summary>Add/edit one dictation app.</summary>
class AppDialog : Form
{
    readonly TextBox name;
    readonly ComboBox process;
    readonly RadioButton whenever, afterShortcut;
    readonly ShortcutBox shortcut;
    readonly Button detect;
    public AppRule Result { get; private set; }

    public AppDialog(AppRule rule)
    {
        Text = string.IsNullOrEmpty(rule.Name) ? "Add app" : "Edit " + rule.Name;
        Font = Ui.Font;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        AutoScaleMode = AutoScaleMode.Font;

        var col = Ui.Column();
        col.Controls.Add(Ui.Text("App", Ui.Heading));
        process = new ComboBox { Width = 260, DropDownStyle = ComboBoxStyle.DropDown, Text = rule.Process };
        process.Items.AddRange(RunningApps());
        name = new TextBox { Width = 200, Text = rule.Name };
        process.TextChanged += delegate { if (name.Tag == null) name.Text = process.Text; UpdateDetect(); };
        name.TextChanged += delegate { if (name.Focused) name.Tag = "edited"; };
        if (!string.IsNullOrEmpty(rule.Name)) name.Tag = "edited";
        col.Controls.Add(Labeled("Process", process, "The program's name, without .exe. Apps using the mic right now are listed first."));
        col.Controls.Add(Labeled("Display name", name, null));

        col.Controls.Add(Ui.Text("When should your call be muted?", Ui.Heading));
        whenever = new RadioButton { Text = "Whenever this app starts using the microphone", AutoSize = true, Margin = new Padding(0, 0, 0, 0) };
        col.Controls.Add(whenever);
        col.Controls.Add(Indent(Ui.Text("Best for apps that only open the mic while you talk to them, like Claude.", null, Ui.Muted)));
        afterShortcut = new RadioButton { Text = "When I press a shortcut and the app then starts listening", AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
        col.Controls.Add(afterShortcut);
        col.Controls.Add(Indent(Ui.Text(
            "Use this for apps that also use the mic for other things, like Wispr Flow's meeting recorder. " +
            "Enter the same shortcut you use in that app; DictaMute only listens, the app still gets it.", null, Ui.Muted)));

        shortcut = new ShortcutBox { Width = 240, Value = rule.Shortcut };
        detect = Ui.Button("Use Wispr Flow's shortcut", delegate
        {
            string found = Shortcut.FromWisprConfig();
            if (found != null) shortcut.Value = found;
            else MessageBox.Show(this, "Couldn't read a push-to-talk shortcut from Wispr Flow's settings.", "DictaMute");
        });
        detect.Margin = new Padding(8, 0, 0, 0);
        col.Controls.Add(Indent(Ui.Row(shortcut, detect)));
        shortcut.Enter += delegate { afterShortcut.Checked = true; };

        bool hasShortcut = Shortcut.Parse(rule.Shortcut).Count > 0;
        whenever.Checked = !hasShortcut;
        afterShortcut.Checked = hasShortcut;
        UpdateDetect();

        var ok = Ui.Button("OK", delegate { Accept(); });
        var cancel = Ui.Button("Cancel", delegate { Close(); });
        cancel.Margin = new Padding(0);
        var buttons = Ui.Row(ok, cancel);
        buttons.Anchor = AnchorStyles.Right;
        buttons.Margin = new Padding(0, 12, 0, 0);
        col.Controls.Add(buttons);
        AcceptButton = ok; CancelButton = cancel;
        Controls.Add(col);
        Result = rule;
    }

    void UpdateDetect()
    {
        detect.Visible = process.Text.StartsWith("Wispr", StringComparison.OrdinalIgnoreCase);
    }

    void Accept()
    {
        string proc = process.Text.Trim();
        if (proc.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) proc = proc.Substring(0, proc.Length - 4);
        if (proc.Length == 0) { MessageBox.Show(this, "Choose or type the app's process name.", "DictaMute"); return; }
        if (afterShortcut.Checked && Shortcut.Parse(shortcut.Value).Count == 0)
        {
            MessageBox.Show(this, "Click the shortcut box and press your shortcut, or choose \"Whenever this app starts using the microphone\".", "DictaMute");
            return;
        }
        Result = new AppRule
        {
            Process = proc,
            Name = name.Text.Trim().Length > 0 ? name.Text.Trim() : proc,
            Enabled = Result.Enabled,
            Shortcut = afterShortcut.Checked ? shortcut.Value : "",
        };
        DialogResult = DialogResult.OK;
        Close();
    }

    static Control Labeled(string label, Control field, string hint)
    {
        var l = Ui.Text(label);
        l.MinimumSize = new Size(90, 0); l.Margin = new Padding(0, 4, 8, 0);
        var stack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        field.Margin = new Padding(0, 0, 0, 2);
        stack.Controls.Add(field);
        if (hint != null) { var h = Ui.Text(hint, null, Ui.Muted); h.MaximumSize = new Size(420, 0); stack.Controls.Add(h); }
        return Ui.Row(l, stack);
    }

    static Control Indent(Control c) { c.Margin = new Padding(20, c.Margin.Top, 0, c.Margin.Bottom); return c; }

    static object[] RunningApps()
    {
        var usingMic = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var pid in AudioCapture.ActivePids())
                try { usingMic.Add(Process.GetProcessById((int)pid).ProcessName); } catch { }
        }
        catch { }
        var withWindows = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
            try { if (p.MainWindowHandle != IntPtr.Zero) withWindows.Add(p.ProcessName); } catch { }
        return usingMic.OrderBy(n => n)
            .Concat(withWindows.Where(n => !usingMic.Contains(n)).OrderBy(n => n))
            .Cast<object>().ToArray();
    }
}

/// <summary>
/// Click it, press a key combo or a middle/thumb mouse button, release: that's the shortcut.
/// Uses DictaMute's global hooks so combos like Ctrl+Win don't open the Start menu while recording.
/// Esc cancels, Backspace clears.
/// </summary>
class ShortcutBox : TextBox
{
    readonly HashSet<int> held = new HashSet<int>();
    readonly HashSet<int> peak = new HashSet<int>();
    string value = "";

    public ShortcutBox()
    {
        ReadOnly = true;
        BackColor = SystemColors.Window;
        Cursor = Cursors.Hand;
    }

    public string Value
    {
        get { return value; }
        set { this.value = value ?? ""; if (!Focused) Text = Display(); }
    }

    string Display() { return value.Length > 0 ? Shortcut.Pretty(value) : "Click, then press your shortcut"; }

    protected override void OnEnter(EventArgs e)
    {
        base.OnEnter(e);
        held.Clear(); peak.Clear();
        Hooks.Recorder = OnInput;
        Text = "Press your shortcut…";
    }

    protected override void OnLeave(EventArgs e)
    {
        base.OnLeave(e);
        if (Hooks.Recorder == OnInput) Hooks.Recorder = null;
        Text = Display();
    }

    // Returns true to swallow the input while recording.
    bool OnInput(int vk, bool down)
    {
        if (down)
        {
            if (held.Count == 0 && vk == 0x09) return false;                       // Tab: move on
            if (held.Count == 0 && vk == 0x1B) { Parent.SelectNextControl(this, true, true, true, true); return true; } // Esc
            if (held.Count == 0 && vk == 0x08) { value = ""; Text = "Press your shortcut…"; return true; } // Backspace
            held.Add(vk);
            peak.UnionWith(held);
            Text = Shortcut.Format(peak) + " …";
        }
        else if (held.Remove(vk) && held.Count == 0 && peak.Count > 0)
        {
            value = Shortcut.Format(peak);
            peak.Clear();
            Text = Shortcut.Pretty(value);
        }
        return true;
    }
}
