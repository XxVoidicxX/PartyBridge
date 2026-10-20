using System.Text;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using PartyBridge.Audio;

namespace PartyBridge;

/// <summary>
/// PartyBridge.exe --selftest out.txt [tones]
/// Lists devices and audio apps, checks per-app capture of Discord and the Xbox party, and runs the
/// full bridge for a few seconds. With "tones" it also plays a quiet tone into each virtual cable and
/// checks it comes out the other side (this is audible to anyone using that cable as a mic).
/// </summary>
static class SelfTest
{
    public static void Run(string path, bool tones)
    {
        var sb = new StringBuilder();
        void L(string s) { sb.AppendLine(s); File.WriteAllText(path, sb.ToString()); }

        L($"PartyBridge self-test {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        using var en = new MMDeviceEnumerator();
        L("\n[render devices]");
        foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            L($"  {d.FriendlyName}  ({d.AudioClient.MixFormat.SampleRate} Hz, {d.AudioClient.MixFormat.Channels} ch)");
        L("\n[capture devices]");
        foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            L($"  {d.FriendlyName}  ({d.AudioClient.MixFormat.SampleRate} Hz, {d.AudioClient.MixFormat.Channels} ch)");
        L("\n[apps with audio sessions]");
        foreach (var n in Procs.AudioSessionApps()) L("  " + n);

        var cfg = Defaults.Build(new Bridge());
        L($"\n[defaults] mic={Name(en, cfg.MicId)} toXbox={Name(en, cfg.ToXboxId)} toDiscord={Name(en, cfg.ToDiscordId)}");
        L($"  Xbox party mic should be: {Defaults.CaptureSideOf(Name(en, cfg.ToXboxId))}");
        L($"  Discord input should be:  {Defaults.CaptureSideOf(Name(en, cfg.ToDiscordId))}");

        if (tones)
        {
            L("\n[cable loopback tones]");
            foreach (var id in new[] { cfg.ToXboxId, cfg.ToDiscordId })
                L("  " + CableTest(en, id));
        }

        L("\n[per-app capture, 3 s each]");
        foreach (var app in new[] { cfg.DiscordApp, cfg.XboxApp })
        {
            using var s = new AppSource(app);
            string fault = null;
            s.Faulted += m => fault = m;
            s.Start();
            float peak = 0;
            for (int i = 0; i < 30; i++) { Thread.Sleep(100); peak = Math.Max(peak, s.Meter.Take()); }
            L($"  {app}: pid={s.Pid} status='{s.Status}' packets={s.Packets} peak={Db(peak)}{(fault != null ? " fault=" + fault : "")}");
        }

        L("\n[full bridge, 4 s]");
        using (var b = new Bridge())
        {
            var faults = new List<string>();
            b.Faulted += m => { lock (faults) faults.Add(m); };
            try
            {
                b.Start(cfg);
                float pm = 0, pd = 0, px = 0, ox = 0, od = 0;
                for (int i = 0; i < 40; i++)
                {
                    Thread.Sleep(100);
                    pm = Math.Max(pm, b.Mic.Meter.Take()); pd = Math.Max(pd, b.Discord.Meter.Take()); px = Math.Max(px, b.Xbox.Meter.Take());
                    ox = Math.Max(ox, b.ToXboxMeter.Take()); od = Math.Max(od, b.ToDiscordMeter.Take());
                }
                L($"  mic peak {Db(pm)}, discord peak {Db(pd)} ({b.Discord.Status}), xbox peak {Db(px)} ({b.Xbox.Status})");
                L($"  to-xbox out peak {Db(ox)}, to-discord out peak {Db(od)}, underruns {b.Underruns}");
            }
            catch (Exception ex) { L("  start failed: " + ex.Message); }
            lock (faults) foreach (var f in faults) L("  fault: " + f);
        }
        L("\ndone");
    }

    static string Name(MMDeviceEnumerator en, string id)
    {
        try { return en.GetDevice(id).FriendlyName; } catch { return "(none)"; }
    }

    static string Db(float p) => p <= 1e-6f ? "silent" : $"{20 * Math.Log10(p):0.0} dBFS";

    /// <summary>Plays 1 kHz at -30 dBFS into a render device and listens on its paired capture device.</summary>
    static string CableTest(MMDeviceEnumerator en, string renderId)
    {
        MMDevice render;
        try { render = en.GetDevice(renderId); } catch { return "no device"; }
        string capName = Defaults.CaptureSideOf(render.FriendlyName);
        var cap = en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).FirstOrDefault(d => d.FriendlyName == capName);
        if (cap == null) return $"{render.FriendlyName}: paired capture '{capName}' not found";

        var mix = render.AudioClient.MixFormat;
        var tone = new SignalGenerator(mix.SampleRate, mix.Channels) { Frequency = 1000, Gain = 0.0316, Type = SignalGeneratorType.Sin };
        using var wo = new WasapiOut(render, AudioClientShareMode.Shared, true, 40);
        using var wc = new WasapiCapture(cap, true, 40);
        double sum = 0; long n = 0; int crossings = 0; float prev = 0;
        float[] mono = new float[8192];
        var fmt = wc.WaveFormat;
        wc.DataAvailable += (_, e) =>
        {
            int ch = fmt.Channels, frames = e.BytesRecorded / fmt.BlockAlign;
            var f32 = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(e.Buffer.AsSpan(0, frames * fmt.BlockAlign));
            for (int i = 0; i < frames; i++)
            {
                float x = fmt.BitsPerSample == 32 ? f32[i * ch] : 0;
                sum += x * x; n++;
                if (prev <= 0 && x > 0) crossings++;
                prev = x;
            }
        };
        wo.Init(tone.ToWaveProvider());
        wc.StartRecording();
        Thread.Sleep(300);
        sum = 0; n = 0; crossings = 0;
        wo.Play();
        Thread.Sleep(300);
        sum = 0; n = 0; crossings = 0;
        Thread.Sleep(1000);
        double secs = n / (double)fmt.SampleRate;
        double rms = n > 0 ? Math.Sqrt(sum / n) : 0;
        wo.Stop(); wc.StopRecording();
        double hz = secs > 0 ? crossings / secs : 0;
        bool ok = rms > 0.005 && Math.Abs(hz - 1000) < 50;
        return $"{render.FriendlyName} -> {capName}: rms {20 * Math.Log10(Math.Max(rms, 1e-9)):0.0} dBFS, ~{hz:0} Hz  {(ok ? "OK" : "FAIL")}";
    }
}
