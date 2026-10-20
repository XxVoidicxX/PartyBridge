using System.Text;
using NAudio.CoreAudioApi;
using PartyBridge.Audio;

namespace PartyBridge;

/// <summary>PartyBridge.exe --probe out.txt [app ...]: endpoint levels, default devices, and per-app capture peaks.</summary>
static class Probe
{
    public static void Run(string path, string[] apps)
    {
        var sb = new StringBuilder();
        using var en = new MMDeviceEnumerator();
        foreach (var flow in new[] { DataFlow.Capture, DataFlow.Render })
            foreach (var d in en.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                var v = d.AudioEndpointVolume;
                sb.AppendLine($"{flow} {d.FriendlyName}: scalar {v.MasterVolumeLevelScalar:0.00} ({v.MasterVolumeLevel:0.0} dB, range {v.VolumeRange.MinDecibels:0}..{v.VolumeRange.MaxDecibels:0}) mute={v.Mute}");
            }
        foreach (var role in new[] { Role.Console, Role.Communications })
        {
            try { sb.AppendLine($"default capture {role}: {en.GetDefaultAudioEndpoint(DataFlow.Capture, role).FriendlyName}"); } catch { }
            try { sb.AppendLine($"default render {role}: {en.GetDefaultAudioEndpoint(DataFlow.Render, role).FriendlyName}"); } catch { }
        }
        foreach (var c in CaptureSessions.List()) sb.AppendLine($"capture session: {c.ProcessName} ({c.Pid}) on {c.DeviceName} active={c.Active}");
        sb.AppendLine("[per-app device overrides]");
        var names = new Dictionary<string, string>();
        foreach (var flow in new[] { DataFlow.Render, DataFlow.Capture })
            foreach (var d in en.EnumerateAudioEndPoints(flow, DeviceState.All)) names[d.ID] = d.FriendlyName;
        foreach (var proc in System.Diagnostics.Process.GetProcesses().GroupBy(p => p.ProcessName).Select(g => g.First()))
        {
            for (int flow = 0; flow < 2; flow++)
                for (int role = 0; role < 3; role++)
                {
                    string id;
                    try { id = AppRouting.GetPersisted(proc.Id, flow, role); } catch { continue; }
                    if (id != "") sb.AppendLine($"  {proc.ProcessName} {(flow == 0 ? "output" : "input")} role{role}: {names.GetValueOrDefault(id, id)}");
                }
        }
        foreach (var app in apps)
        {
            using Source s = app.StartsWith("dev:")
                ? new DeviceLoopbackSource(en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).First(d => d.FriendlyName == app[4..]))
                : new AppSource(app);
            s.Start();
            float peak = 0;
            for (int i = 0; i < 30; i++) { Thread.Sleep(100); peak = Math.Max(peak, s.Meter.Take()); }
            sb.AppendLine($"{app}: pid={(s as AppSource)?.Pid} status='{s.Status}' packets={s.Packets} peak={(peak <= 1e-6 ? "silent" : $"{20 * Math.Log10(peak):0.0} dBFS")}");
        }
        File.WriteAllText(path, sb.ToString());
    }
}
