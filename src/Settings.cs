// Settings: which dictation apps DictaMute watches, and how each one "starts".
// Stored as JSON in %LOCALAPPDATA%\DictaMute\settings.json.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using System.Windows.Forms;

/// <summary>One dictation app. Shortcut "" = mute whenever it starts using the microphone.</summary>
class AppRule
{
    public string Name { get; set; }
    public string Process { get; set; }   // process name without ".exe", e.g. "Wispr Flow", "claude"
    public bool Enabled { get; set; }
    public string Shortcut { get; set; }  // one or more combos, comma-separated: "Mouse5, Ctrl+Win"

    public AppRule() { Name = ""; Process = ""; Enabled = true; Shortcut = ""; }

    public AppRule Clone()
    {
        return new AppRule { Name = Name, Process = Process, Enabled = Enabled, Shortcut = Shortcut };
    }

    public bool Matches(string processName)
    {
        return processName.Equals(Process, StringComparison.OrdinalIgnoreCase)
            || processName.StartsWith(Process + " ", StringComparison.OrdinalIgnoreCase);
    }
}

class Settings
{
    public List<AppRule> Apps { get; set; }
    public int UnmuteDelayMs { get; set; }  // how long an app's mic must be idle before the call is unmuted
    /// <summary>Call apps to mute, by CallMic.Name ("Teams", "Discord"). null = all of them.</summary>
    public List<string> CallApps { get; set; }

    public Settings() { Apps = new List<AppRule>(); UnmuteDelayMs = 600; }

    public bool Mutes(string callApp)
    {
        return CallApps == null || CallApps.Contains(callApp, StringComparer.OrdinalIgnoreCase);
    }

    public static string Dir
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DictaMute"); }
    }
    static string FilePath { get { return Path.Combine(Dir, "settings.json"); } }

    public static bool Exists { get { return File.Exists(FilePath); } }

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(FilePath));
                if (s != null && s.Apps != null)
                {
                    if (s.UnmuteDelayMs < 100 || s.UnmuteDelayMs > 10000) s.UnmuteDelayMs = 600;
                    return s;
                }
            }
        }
        catch (Exception) { }
        return Defaults();
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, new JavaScriptSerializer().Serialize(this));
    }

    public Settings Clone()
    {
        return new Settings
        {
            Apps = Apps.Select(a => a.Clone()).ToList(),
            UnmuteDelayMs = UnmuteDelayMs,
            CallApps = CallApps == null ? null : new List<string>(CallApps),
        };
    }

    public static Settings Defaults()
    {
        var s = new Settings();
        s.Apps.Add(new AppRule
        {
            Name = "Wispr Flow",
            Process = "Wispr Flow",
            Shortcut = Shortcut.FromWisprConfig() ?? "Ctrl+Win", // Wispr's own default push-to-talk
        });
        s.Apps.Add(new AppRule { Name = "Claude", Process = "claude", Shortcut = "" });
        return s;
    }
}

/// <summary>
/// Shortcut combos as sets of virtual-key codes. Left/right modifiers are folded together,
/// and the middle/thumb mouse buttons use their VK codes so keys and buttons mix freely.
/// </summary>
static class Shortcut
{
    public const int Mouse3 = 0x04, Mouse4 = 0x05, Mouse5 = 0x06;
    const int Shift = 0x10, Ctrl = 0x11, Alt = 0x12, Win = 0x5B;

    public static int Canonical(int vk)
    {
        switch (vk)
        {
            case 0xA0: case 0xA1: return Shift;
            case 0xA2: case 0xA3: return Ctrl;
            case 0xA4: case 0xA5: return Alt;
            case 0x5C: return Win;
        }
        return vk;
    }

    /// <summary>"Mouse5, Ctrl+Win" -> two combos. Unknown names are skipped.</summary>
    public static List<HashSet<int>> Parse(string text)
    {
        var combos = new List<HashSet<int>>();
        if (string.IsNullOrWhiteSpace(text)) return combos;
        foreach (var part in text.Split(','))
        {
            var combo = new HashSet<int>();
            foreach (var raw in part.Split('+'))
            {
                int vk = Code(raw.Trim());
                if (vk > 0) combo.Add(vk);
            }
            if (combo.Count > 0) combos.Add(combo);
        }
        return combos;
    }

    public static string Format(IEnumerable<int> combo)
    {
        return string.Join("+", combo.OrderBy(Rank).Select(Name));
    }

    /// <summary>Human-readable, for the settings list: "Mouse button 5 (forward thumb)".</summary>
    public static string Describe(string text)
    {
        if (Parse(text).Count == 0) return "Whenever it uses the mic";
        string pretty = Pretty(text);
        if (pretty.StartsWith("Mouse") || pretty.StartsWith("Middle")) pretty = char.ToLower(pretty[0]) + pretty.Substring(1);
        return "After " + pretty;
    }

    /// <summary>"Mouse5, Ctrl+Win" -> "Mouse button 5 (forward thumb) or Ctrl + Win".</summary>
    public static string Pretty(string text)
    {
        return string.Join(" or ", Parse(text).Select(c => string.Join(" + ", c.OrderBy(Rank).Select(Friendly))));
    }

    static int Rank(int vk)
    {
        switch (vk)
        {
            case Ctrl: return 0;
            case Shift: return 1;
            case Alt: return 2;
            case Win: return 3;
            case Mouse3: case Mouse4: case Mouse5: return 5;
        }
        return 4;
    }

    static string Name(int vk)
    {
        switch (vk)
        {
            case Ctrl: return "Ctrl";
            case Shift: return "Shift";
            case Alt: return "Alt";
            case Win: return "Win";
            case Mouse3: return "Mouse3";
            case Mouse4: return "Mouse4";
            case Mouse5: return "Mouse5";
        }
        return ((Keys)vk).ToString();
    }

    static string Friendly(int vk)
    {
        switch (vk)
        {
            case Mouse3: return "Middle mouse button";
            case Mouse4: return "Mouse button 4 (back thumb)";
            case Mouse5: return "Mouse button 5 (forward thumb)";
            case 0x14: return "Caps Lock";
            case 0x20: return "Space";
        }
        return Name(vk);
    }

    static int Code(string name)
    {
        switch (name.ToLowerInvariant())
        {
            case "ctrl": case "control": return Ctrl;
            case "shift": return Shift;
            case "alt": return Alt;
            case "win": case "windows": return Win;
            case "mouse3": return Mouse3;
            case "mouse4": return Mouse4;
            case "mouse5": return Mouse5;
        }
        Keys key;
        if (Enum.TryParse(name, true, out key)) return Canonical((int)key & 0xFF);
        return 0;
    }

    /// <summary>
    /// Wispr Flow's push-to-talk shortcut(s), read from its config.json. Wispr stores combos as
    /// "162+91" (virtual-key codes) and mouse buttons as 4096 + button index (4100 = button 5).
    /// </summary>
    public static string FromWisprConfig()
    {
        try
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Wispr Flow", "config.json");
            if (!File.Exists(path)) return null;
            var root = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>;
            var shortcuts = Dig(root, "prefs", "user", "shortcuts");
            if (shortcuts == null) return null;
            var combos = new List<string>();
            foreach (var entry in shortcuts)
            {
                if (!"ptt".Equals(entry.Value as string)) continue;
                var combo = new HashSet<int>();
                bool ok = true;
                foreach (var part in entry.Key.Split('+'))
                {
                    int code;
                    if (!int.TryParse(part, out code)) { ok = false; break; }
                    if (code >= 4096)
                    {
                        int button = code - 4096; // 0 left, 1 right, 2 middle, 3 back, 4 forward
                        if (button == 2) combo.Add(Mouse3);
                        else if (button == 3) combo.Add(Mouse4);
                        else if (button == 4) combo.Add(Mouse5);
                        else { ok = false; break; }
                    }
                    else combo.Add(Canonical(code));
                }
                if (ok && combo.Count > 0) combos.Add(Format(combo));
            }
            return combos.Count > 0 ? string.Join(", ", combos) : null;
        }
        catch (Exception) { return null; }
    }

    static Dictionary<string, object> Dig(Dictionary<string, object> node, params string[] keys)
    {
        foreach (var key in keys)
        {
            object next;
            if (node == null || !node.TryGetValue(key, out next)) return null;
            node = next as Dictionary<string, object>;
        }
        return node;
    }
}
