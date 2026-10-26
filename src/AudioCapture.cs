// Core Audio interop: which processes have an *active* microphone capture stream right now.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

static class AudioCapture
{
    const int eCapture = 1;
    const int DEVICE_STATE_ACTIVE = 1;
    const int AudioSessionStateActive = 1;

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        int GetCount(out int count);
        int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        int GetAudioSessionControl(IntPtr sessionGuid, int flags, out IntPtr control);
        int GetSimpleAudioVolume(IntPtr sessionGuid, int flags, out IntPtr volume);
        int GetSessionEnumerator(out IAudioSessionEnumerator enumerator);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        int GetCount(out int count);
        int GetSession(int index, out IAudioSessionControl2 session);
    }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        // IAudioSessionControl
        int GetState(out int state);
        int GetDisplayName(out IntPtr name);
        int SetDisplayName(string name, ref Guid ctx);
        int GetIconPath(out IntPtr path);
        int SetIconPath(string path, ref Guid ctx);
        int GetGroupingParam(out Guid param);
        int SetGroupingParam(ref Guid param, ref Guid ctx);
        int RegisterAudioSessionNotification(IntPtr client);
        int UnregisterAudioSessionNotification(IntPtr client);
        // IAudioSessionControl2
        int GetSessionIdentifier(out IntPtr id);
        int GetSessionInstanceIdentifier(out IntPtr id);
        int GetProcessId(out uint pid);
        int IsSystemSoundsSession();
        int SetDuckingPreference(bool optOut);
    }

    /// <summary>"process(pid)" for every capture session currently in the Active state, all mics.</summary>
    public static List<string> ActiveSessions()
    {
        var result = new List<string>();
        foreach (var pid in ActivePids())
        {
            string name;
            try { name = Process.GetProcessById((int)pid).ProcessName; } catch { name = "?"; }
            result.Add(name + "(" + pid + ")");
        }
        return result;
    }

    /// <summary>
    /// Every capture session on every mic, active or not: "pid|state|instance-id". The instance id
    /// tells two streams of the same app apart (e.g. a recorder stream and a dictation stream).
    /// </summary>
    public static List<string> AllSessions()
    {
        var result = new List<string>();
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        IMMDeviceCollection devices;
        enumerator.EnumAudioEndpoints(eCapture, DEVICE_STATE_ACTIVE, out devices);
        int count; devices.GetCount(out count);
        var iid = typeof(IAudioSessionManager2).GUID;
        for (int i = 0; i < count; i++)
        {
            IMMDevice device; devices.Item(i, out device);
            object o;
            if (device.Activate(ref iid, 23, IntPtr.Zero, out o) != 0) continue;
            var manager = (IAudioSessionManager2)o;
            IAudioSessionEnumerator sessions;
            if (manager.GetSessionEnumerator(out sessions) != 0) continue;
            int n; sessions.GetCount(out n);
            for (int j = 0; j < n; j++)
            {
                IAudioSessionControl2 s; sessions.GetSession(j, out s);
                int state; s.GetState(out state);
                uint pid; s.GetProcessId(out pid);
                IntPtr idPtr; s.GetSessionInstanceIdentifier(out idPtr);
                string id = Marshal.PtrToStringUni(idPtr) ?? "";
                Marshal.FreeCoTaskMem(idPtr);
                result.Add(pid + "|" + state + "|" + id);
                Marshal.ReleaseComObject(s);
            }
            Marshal.ReleaseComObject(sessions);
            Marshal.ReleaseComObject(manager);
            Marshal.ReleaseComObject(device);
        }
        Marshal.ReleaseComObject(devices);
        Marshal.ReleaseComObject(enumerator);
        return result;
    }

    public static List<uint> ActivePids()
    {
        var pids = new List<uint>();
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        IMMDeviceCollection devices;
        enumerator.EnumAudioEndpoints(eCapture, DEVICE_STATE_ACTIVE, out devices);
        int count; devices.GetCount(out count);
        var iid = typeof(IAudioSessionManager2).GUID;
        for (int i = 0; i < count; i++)
        {
            IMMDevice device; devices.Item(i, out device);
            object o;
            if (device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out o) != 0) continue;
            var manager = (IAudioSessionManager2)o;
            IAudioSessionEnumerator sessions;
            if (manager.GetSessionEnumerator(out sessions) != 0) continue;
            int n; sessions.GetCount(out n);
            for (int j = 0; j < n; j++)
            {
                IAudioSessionControl2 s; sessions.GetSession(j, out s);
                int state; s.GetState(out state);
                uint pid; s.GetProcessId(out pid);
                if (state == AudioSessionStateActive && !pids.Contains(pid)) pids.Add(pid);
                Marshal.ReleaseComObject(s);
            }
            Marshal.ReleaseComObject(sessions);
            Marshal.ReleaseComObject(manager);
            Marshal.ReleaseComObject(device);
        }
        Marshal.ReleaseComObject(devices);
        Marshal.ReleaseComObject(enumerator);
        return pids;
    }
}
