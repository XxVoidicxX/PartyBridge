using System.Text.Json;
using NAudio.CoreAudioApi;

namespace PartyBridge.Audio;

/// <summary>
/// System changes PartyBridge makes while running (a virtual mic's recording level, the Xbox app's output
/// device). Each one is written to restore.json before it is made, undone on STOP, and undone on the next
/// launch if PartyBridge was killed or the PC restarted while it ran.
/// </summary>
public sealed class Restore
{
    public sealed class Entry
    {
        public string Kind { get; set; }          // "level" or "route"
        public string DeviceId { get; set; }      // level: capture device; route: original output ("" = default)
        public float Db { get; set; }             // level: original recording level
        public bool Muted { get; set; }           // level: original mute
        public List<string> Apps { get; set; }    // route: process names of the app
    }

    public List<Entry> Entries { get; set; } = new();

    static readonly object Gate = new();
    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PartyBridge", "restore.json");

    static Restore Load()
    {
        try { return JsonSerializer.Deserialize<Restore>(File.ReadAllText(FilePath)) ?? new Restore(); }
        catch { return new Restore(); }
    }

    void Save()
    {
        try
        {
            if (Entries.Count == 0) { File.Delete(FilePath); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    /// <summary>Records an original value, unless one for the same target is already pending (that one is the real original).</summary>
    public static void Remember(Entry e)
    {
        lock (Gate)
        {
            var r = Load();
            bool dup = r.Entries.Any(x => x.Kind == e.Kind &&
                (e.Kind == "level" ? x.DeviceId == e.DeviceId : x.Apps.Intersect(e.Apps, StringComparer.OrdinalIgnoreCase).Any()));
            if (!dup) { r.Entries.Add(e); r.Save(); }
        }
    }

    /// <summary>Puts back everything recorded. Returns what it did. Entries it can't apply yet (app not running) are kept.</summary>
    public static List<string> ApplyAll()
    {
        lock (Gate)
        {
            var done = new List<string>();
            var r = Load();
            using var en = new MMDeviceEnumerator();
            foreach (var e in r.Entries.ToList())
            {
                try
                {
                    if (e.Kind == "level")
                    {
                        var dev = en.GetDevice(e.DeviceId);
                        dev.AudioEndpointVolume.MasterVolumeLevel = e.Db;
                        dev.AudioEndpointVolume.Mute = e.Muted;
                        done.Add($"{dev.FriendlyName} level back to {e.Db:+0;-0} dB");
                        r.Entries.Remove(e);
                    }
                    else if (e.Kind == "route")
                    {
                        int pid = e.Apps.Select(Procs.FindRoot).FirstOrDefault(p => p > 0, -1);
                        if (pid <= 0) continue;
                        AppRouting.SetOutput(pid, e.DeviceId);
                        done.Add($"{Procs.NameOf(pid)} output back to {(string.IsNullOrEmpty(e.DeviceId) ? "Default" : en.GetDevice(e.DeviceId).FriendlyName)}");
                        r.Entries.Remove(e);
                    }
                }
                catch { if (e.Kind == "level") r.Entries.Remove(e); }
            }
            r.Save();
            return done;
        }
    }
}
