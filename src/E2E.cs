using NAudio.CoreAudioApi;
using NAudio.Wave;
using PartyBridge.Audio;

namespace PartyBridge;

/// <summary>
/// PartyBridge.exe --e2e out.txt StandInApp: runs the real bridge with StandInApp in Discord's place and
/// records what comes out of both cables' recording sides, the same way Xbox and Discord would hear it.
/// </summary>
static class E2E
{
    public static void Run(string path, string standIn)
    {
        var lines = new List<string>();
        using var b = new Bridge();
        var cfg = Defaults.Build(b);
        cfg.DiscordApp = standIn;
        b.Faulted += m => { lock (lines) lines.Add("fault: " + m); };
        var notes = b.Start(cfg);
        lines.AddRange(notes.Select(n => "note: " + n));

        var caps = new[] { b.PairedCapture(cfg.ToXboxId), b.PairedCapture(cfg.ToDiscordId) };
        var meters = new[] { new Meter(), new Meter() };
        var recs = new List<WasapiCapture>();
        for (int k = 0; k < 2; k++)
        {
            var m = meters[k];
            var wc = new WasapiCapture(caps[k], true, 20);
            var fmt = wc.WaveFormat;
            wc.DataAvailable += (_, e) => m.Push(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(e.Buffer.AsSpan(0, e.BytesRecorded / 4 * 4)));
            wc.StartRecording();
            recs.Add(wc);
        }
        float pm = 0, pd = 0, px = 0, c0 = 0, c1 = 0;
        for (int i = 0; i < 50; i++)
        {
            Thread.Sleep(100);
            pm = Math.Max(pm, b.Mic.Meter.Take()); pd = Math.Max(pd, b.Discord.Meter.Take()); px = Math.Max(px, b.Xbox.Meter.Take());
            c0 = Math.Max(c0, meters[0].Take()); c1 = Math.Max(c1, meters[1].Take());
        }
        foreach (var r in recs) { r.StopRecording(); r.Dispose(); }
        static string Db(float p) => p <= 1e-6f ? "silent" : $"{20 * Math.Log10(p):0.0} dBFS";
        lines.Add($"sources: mic {Db(pm)}, {standIn} (as Discord) {Db(pd)} [{b.Discord.Status}], xbox {Db(px)} [{b.Xbox.Status}]");
        lines.Add($"heard on {caps[0].FriendlyName} (Xbox mic): {Db(c0)}");
        lines.Add($"heard on {caps[1].FriendlyName} (Discord mic): {Db(c1)}");
        lines.Add($"underruns {b.Underruns}");
        lock (lines) File.WriteAllLines(path, lines);
    }
}
