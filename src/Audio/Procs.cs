using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace PartyBridge.Audio;

static class Procs
{
    [StructLayout(LayoutKind.Sequential)]
    struct ProcessBasicInformation
    {
        public IntPtr ExitStatus, PebBaseAddress, AffinityMask, BasePriority, UniqueProcessId, InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationProcess(IntPtr handle, int infoClass, ref ProcessBasicInformation info, int size, out int returned);

    static int ParentPid(Process p)
    {
        try
        {
            var info = new ProcessBasicInformation();
            if (NtQueryInformationProcess(p.Handle, 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0) return -1;
            return info.InheritedFromUniqueProcessId.ToInt32();
        }
        catch { return -1; }
    }

    /// <summary>
    /// Finds the top process of an app by executable name (no .exe). Apps like Discord run a tree of
    /// same-named processes; loopback in tree mode on the root catches whichever one plays the audio.
    /// </summary>
    public static int FindRoot(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return -1;
        name = name.Trim();
        if (name.StartsWith("pid:") && int.TryParse(name[4..], out int direct)) return IsAlive(direct) ? direct : -1;
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        var procs = Process.GetProcessesByName(name);
        if (procs.Length == 0) return -1;
        var ids = procs.Select(p => p.Id).ToHashSet();
        Process best = null;
        foreach (var p in procs)
        {
            if (!ids.Contains(ParentPid(p)) && (best == null || Started(p) < Started(best))) best = p;
        }
        best ??= procs.OrderBy(Started).First();
        return best.Id;
    }

    static DateTime Started(Process p)
    {
        try { return p.StartTime; } catch { return DateTime.MaxValue; }
    }

    public static bool IsAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }

    public static string NameOf(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.ProcessName; }
        catch { return null; }
    }

    /// <summary>Process names that currently have an audio session on any active output device.</summary>
    public static List<string> AudioSessionApps()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var en = new MMDeviceEnumerator();
            foreach (var dev in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try
                {
                    var sessions = dev.AudioSessionManager.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        uint pid = sessions[i].GetProcessID;
                        if (pid == 0) continue;
                        var n = NameOf((int)pid);
                        if (n != null) names.Add(n);
                    }
                }
                catch { }
            }
        }
        catch { }
        return names.ToList();
    }
}

public sealed record CaptureUse(string ProcessName, int Pid, string DeviceId, string DeviceName, bool Active);

static class CaptureSessions
{
    /// <summary>Every app that has a recording session open, and on which mic. Active = capturing right now.</summary>
    public static List<CaptureUse> List(DataFlow flow = DataFlow.Capture)
    {
        var list = new List<CaptureUse>();
        try
        {
            using var en = new MMDeviceEnumerator();
            foreach (var dev in en.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                try
                {
                    var sessions = dev.AudioSessionManager.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        var s = sessions[i];
                        int pid = (int)s.GetProcessID;
                        if (pid == 0) continue;
                        var n = Procs.NameOf(pid);
                        if (n == null) continue;
                        list.Add(new CaptureUse(n, pid, dev.ID, dev.FriendlyName, s.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive));
                    }
                }
                catch { }
            }
        }
        catch { }
        return list;
    }
}
