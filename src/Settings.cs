using System.Text.Json;
using NAudio.CoreAudioApi;
using PartyBridge.Audio;

namespace PartyBridge;

public sealed class Settings
{
    public BridgeConfig Bridge { get; set; } = new();
    public float MicGainDb { get; set; }
    public float DiscordGainDb { get; set; }
    public float XboxGainDb { get; set; }
    public bool MicMuted { get; set; }
    public bool AutoStart { get; set; }

    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PartyBridge", "settings.json");

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings(); }
        catch { return new Settings(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

static class Defaults
{
    static readonly string[] VirtualHints = { "CABLE", "VB-Audio", "Steam Streaming", "VoiceMeeter", "Voicemod" };

    public static bool IsVirtual(string name) => VirtualHints.Any(h => name.Contains(h, StringComparison.OrdinalIgnoreCase));

    /// <summary>The recording device an app should pick to hear what we send into a virtual output.</summary>
    public static string CaptureSideOf(string renderName)
    {
        if (string.IsNullOrEmpty(renderName)) return "";
        if (renderName.StartsWith("Speakers (", StringComparison.OrdinalIgnoreCase))
            return "Microphone (" + renderName["Speakers (".Length..];
        int i = renderName.IndexOf(" Input", StringComparison.OrdinalIgnoreCase);
        return i >= 0 ? renderName[..i] + " Output" + renderName[(i + 6)..] : renderName;
    }

    /// <summary>Fills in any missing devices: real mic, VB-Cable toward Xbox, Steam Streaming Microphone toward Discord.</summary>
    public static BridgeConfig Build(Bridge b, BridgeConfig c = null)
    {
        c ??= new BridgeConfig();
        var caps = b.GetDevices(DataFlow.Capture);
        var rends = b.GetDevices(DataFlow.Render);

        if (!caps.Any(d => d.Id == c.MicId))
        {
            string def = b.DefaultId(DataFlow.Capture);
            var defDev = caps.FirstOrDefault(d => d.Id == def);
            c.MicId = defDev != null && !IsVirtual(defDev.Name) ? def : caps.FirstOrDefault(d => !IsVirtual(d.Name))?.Id ?? def;
        }
        if (!rends.Any(d => d.Id == c.ToXboxId))
            c.ToXboxId = rends.FirstOrDefault(d => d.Name.StartsWith("CABLE Input", StringComparison.OrdinalIgnoreCase))?.Id
                         ?? rends.FirstOrDefault(d => IsVirtual(d.Name))?.Id;
        if (!rends.Any(d => d.Id == c.ToDiscordId) || c.ToDiscordId == c.ToXboxId)
            c.ToDiscordId = rends.FirstOrDefault(d => d.Name == "Speakers (Steam Streaming Microphone)")?.Id
                            ?? rends.FirstOrDefault(d => IsVirtual(d.Name) && d.Id != c.ToXboxId && caps.Any(k => k.Name == CaptureSideOf(d.Name)))?.Id;
        if (c.XboxSpeakerId == null || (c.XboxSpeakerId != "" && !rends.Any(d => d.Id == c.XboxSpeakerId)))
            c.XboxSpeakerId = rends.FirstOrDefault(d => d.Name == "Speakers (Steam Streaming Speakers)")?.Id ?? "";
        if (!rends.Any(d => d.Id == c.HearXboxOnId))
        {
            string def = b.DefaultId(DataFlow.Render);
            var defDev = rends.FirstOrDefault(d => d.Id == def);
            c.HearXboxOnId = defDev != null && !IsVirtual(defDev.Name) ? def : rends.FirstOrDefault(d => !IsVirtual(d.Name))?.Id ?? def;
        }
        return c;
    }
}
