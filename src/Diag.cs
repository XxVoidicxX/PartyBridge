using System.Text;
using NAudio.CoreAudioApi;
using PartyBridge.Audio;

namespace PartyBridge;

/// <summary>PartyBridge.exe --diag out.txt [seconds]: which apps are actually playing sound, and what each virtual mic carries.</summary>
static class Diag
{
    public static void Run(string path, int seconds)
    {
        var sb = new StringBuilder();
        using var en = new MMDeviceEnumerator();
        var peaks = new Dictionary<string, float>();
        var sessions = new List<(string key, AudioSessionControl s)>();
        foreach (var dev in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            try
            {
                var ss = dev.AudioSessionManager.Sessions;
                for (int i = 0; i < ss.Count; i++)
                {
                    var s = ss[i];
                    int pid = (int)s.GetProcessID;
                    string n = pid == 0 ? "system" : Procs.NameOf(pid) ?? "?";
                    sessions.Add(($"{n} ({pid}) on {dev.FriendlyName} vol={s.SimpleAudioVolume.Volume:0.00} mute={s.SimpleAudioVolume.Mute}", s));
                }
            }
            catch { }
        }
        var capMeters = en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
        var capPeaks = new Dictionary<string, float>();
        var xbox = new AppSource("XboxPartyChatHost");
        xbox.Start();
        float xp = 0;
        for (int t = 0; t < seconds * 10; t++)
        {
            Thread.Sleep(100);
            foreach (var (k, s) in sessions)
            {
                float p = 0; try { p = s.AudioMeterInformation.MasterPeakValue; } catch { }
                peaks[k] = Math.Max(peaks.GetValueOrDefault(k), p);
            }
            foreach (var d in capMeters)
            {
                float p = 0; try { p = d.AudioMeterInformation.MasterPeakValue; } catch { }
                capPeaks[d.FriendlyName] = Math.Max(capPeaks.GetValueOrDefault(d.FriendlyName), p);
            }
            xp = Math.Max(xp, xbox.Meter.Take());
        }
        xbox.Dispose();
        static string Db(float p) => p <= 1e-5f ? "silent" : $"{20 * Math.Log10(p):0.0} dB";
        sb.AppendLine("[render sessions, peak]");
        foreach (var kv in peaks.OrderByDescending(k => k.Value)) sb.AppendLine($"  {Db(kv.Value),-9} {kv.Key}");
        sb.AppendLine("[capture devices, peak]");
        foreach (var kv in capPeaks) sb.AppendLine($"  {Db(kv.Value),-9} {kv.Key}");
        sb.AppendLine($"[loopback XboxPartyChatHost] {Db(xp)} status={xbox.Status} packets={xbox.Packets}");
        sb.AppendLine("[capture sessions]");
        foreach (var c in CaptureSessions.List()) sb.AppendLine($"  {c.ProcessName} ({c.Pid}) on {c.DeviceName} active={c.Active}");
        foreach (var d in capMeters) { var v = d.AudioEndpointVolume; sb.AppendLine($"  level {d.FriendlyName}: {v.MasterVolumeLevel:0.0} dB mute={v.Mute}"); }
        sb.AppendLine("[running PartyBridge]");
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("PartyBridge")) sb.AppendLine($"  pid {p.Id}");
        File.WriteAllText(path, sb.ToString());
    }
}
