// Lists active microphone capture sessions (process + state). Diagnostic for DictaMute.
//   CaptureProbe.exe [seconds]            active apps, printed when they change
//   CaptureProbe.exe [seconds] --streams  every stream of every app (state, instance id)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

static class CaptureProbe
{
    static void Main(string[] args)
    {
        int seconds = args.Length > 0 ? int.Parse(args[0]) : 0;
        bool streams = args.Contains("--streams");
        var end = DateTime.Now.AddSeconds(seconds);
        string last = null;
        do
        {
            string now = streams ? Streams() : string.Join("; ", AudioCapture.ActiveSessions());
            if (now != last) { Console.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff ") + (now.Length > 0 ? now : "(none active)")); last = now; }
            Thread.Sleep(100);
        } while (DateTime.Now < end);
    }

    static readonly Dictionary<string, string> names = new Dictionary<string, string>();

    static string Streams()
    {
        var parts = new List<string>();
        foreach (var s in AudioCapture.AllSessions())
        {
            var f = s.Split('|');
            string name;
            if (!names.TryGetValue(f[0], out name))
            {
                try { name = Process.GetProcessById(int.Parse(f[0])).ProcessName; } catch { name = "?"; }
                names[f[0]] = name;
            }
            if (name == "?" && f[1] != "1") continue; // expired sessions of exited processes
            string state = f[1] == "1" ? "ACTIVE" : f[1] == "0" ? "inactive" : "expired";
            string id = f[2].Length > 12 ? f[2].Substring(f[2].Length - 12) : f[2];
            parts.Add(name + "(" + f[0] + ") " + state + " …" + id);
        }
        parts.Sort();
        return string.Join("\n             ", parts);
    }
}
