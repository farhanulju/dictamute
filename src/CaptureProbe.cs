// Lists active microphone capture sessions (process + state). Diagnostic for TeamsMicHold.
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

static class CaptureProbe
{
    static void Main(string[] args)
    {
        int seconds = args.Length > 0 ? int.Parse(args[0]) : 0;
        var end = DateTime.Now.AddSeconds(seconds);
        string last = null;
        do
        {
            string now = string.Join("; ", AudioCapture.ActiveSessions());
            if (now != last) { Console.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff ") + (now.Length > 0 ? now : "(none active)")); last = now; }
            Thread.Sleep(100);
        } while (DateTime.Now < end);
    }
}
