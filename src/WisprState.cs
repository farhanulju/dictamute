// Wispr Flow's own dictation state. Wispr writes prefs.activeDictationSession into its config.json
// when dictation starts ({"uuid":…,"startTimestamp":…}) and sets it back to null when it stops.
// That's exact even while Wispr's meeting notetaker holds the microphone open for a whole call,
// which makes the "app opened the mic" signal useless for Wispr.
using System;
using System.IO;
using System.Text;

static class WisprState
{
    const string Key = "\"activeDictationSession\"";
    static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Wispr Flow", "config.json");

    static DateTime lastWrite;
    static bool everSeen;  // the field exists in this Wispr version
    static bool dictating;

    public static bool AppliesTo(AppRule rule)
    {
        return rule.Process.StartsWith("Wispr", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>true = dictating, false = idle, null = can't tell (no Wispr, or a version without the field).</summary>
    public static bool? IsDictating()
    {
        try
        {
            var info = new FileInfo(ConfigPath);
            if (!info.Exists) return null;
            if (info.LastWriteTimeUtc != lastWrite)
            {
                string raw;
                using (var stream = new FileStream(ConfigPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                    raw = reader.ReadToEnd();
                int i = raw.IndexOf(Key, StringComparison.Ordinal);
                if (i >= 0)
                {
                    int colon = raw.IndexOf(':', i + Key.Length);
                    int j = colon + 1;
                    while (j < raw.Length && char.IsWhiteSpace(raw[j])) j++;
                    if (colon > 0 && j < raw.Length)
                    {
                        dictating = raw[j] == '{';
                        everSeen = true;
                        lastWrite = info.LastWriteTimeUtc;
                    }
                }
                else if (!everSeen) return null;
                // Key missing after we've seen it = a half-written file: keep the last answer,
                // and leave lastWrite alone so the next poll reads it again.
            }
        }
        catch (IOException) { } // mid-write: keep the last answer
        catch (UnauthorizedAccessException) { }
        return everSeen ? dictating : (bool?)null;
    }
}
