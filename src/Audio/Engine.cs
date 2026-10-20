using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace PartyBridge.Audio;

public sealed record DeviceInfo(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Peak meter shared between an audio thread (writer) and the UI (reader).</summary>
public sealed class Meter
{
    float _peak;
    public void Push(ReadOnlySpan<float> s)
    {
        float p = 0;
        foreach (float x in s) { float a = MathF.Abs(x); if (a > p) p = a; }
        if (p > Volatile.Read(ref _peak)) Volatile.Write(ref _peak, p);
    }
    /// <summary>Returns the peak since the last call and resets it.</summary>
    public float Take() => Interlocked.Exchange(ref _peak, 0);
}

/// <summary>
/// One source feeding one output: a mono ring buffer with a linear resampler from the source
/// rate to the output rate. Absorbs clock drift by skipping ahead when it falls behind.
/// </summary>
sealed class Tap
{
    const int RingSize = 1 << 17, Mask = RingSize - 1;
    readonly float[] _ring = new float[RingSize];
    readonly object _lock = new();
    readonly double _step;
    readonly int _target;
    long _w;
    double _r;
    bool _primed;

    public Tap(int inRate, int outRate, int latencyMs)
    {
        _step = (double)inRate / outRate;
        _target = Math.Max(64, (int)(inRate * latencyMs * 1.5 / 1000));
    }

    public int Underruns { get; private set; }

    public void Write(ReadOnlySpan<float> s)
    {
        lock (_lock)
        {
            foreach (float x in s) { _ring[_w & Mask] = x; _w++; }
        }
    }

    public void ReadAdd(Span<float> dst)
    {
        lock (_lock)
        {
            double avail = _w - _r;
            if (!_primed)
            {
                if (avail < _target) return;
                _primed = true;
            }
            if (avail > _target * 3 + 2048) _r = _w - _target;
            for (int f = 0; f < dst.Length; f++)
            {
                if (_r + 1 >= _w) { _primed = false; Underruns++; return; }
                long i = (long)_r;
                float frac = (float)(_r - i);
                float a = _ring[i & Mask];
                dst[f] += a + (_ring[(i + 1) & Mask] - a) * frac;
                _r += _step;
            }
        }
    }
}

/// <summary>An output device fed by a mix of taps (mono, copied to every channel, soft-limited).</summary>
sealed class Bus : IWaveProvider, IDisposable
{
    readonly List<Tap> _taps = new();
    readonly WasapiOut _out;
    readonly int _ch;
    float[] _mix = new float[8192];

    public Bus(MMDevice dev, int latencyMs)
    {
        var mix = dev.AudioClient.MixFormat;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, mix.Channels);
        _ch = mix.Channels;
        LatencyMs = latencyMs;
        _out = new WasapiOut(dev, AudioClientShareMode.Shared, true, latencyMs);
    }

    public WaveFormat WaveFormat { get; }
    public int SampleRate => WaveFormat.SampleRate;
    public int LatencyMs { get; }
    public Meter Meter { get; } = new();
    public int Underruns => _taps.Sum(t => t.Underruns);
    public event Action<string> Faulted;

    public Tap AddTap(int inRate)
    {
        var t = new Tap(inRate, SampleRate, LatencyMs);
        _taps.Add(t);
        return t;
    }

    public void Start()
    {
        _out.Init(this);
        _out.PlaybackStopped += (_, e) => { if (e.Exception != null) Faulted?.Invoke(e.Exception.Message); };
        _out.Play();
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        var dst = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, count));
        int frames = dst.Length / _ch;
        if (_mix.Length < frames) _mix = new float[frames * 2];
        var m = _mix.AsSpan(0, frames);
        m.Clear();
        foreach (var t in _taps) t.ReadAdd(m);
        for (int i = 0; i < frames; i++)
        {
            // Soft clip above about -3 dBFS so two loud talkers never hard clip the virtual mic.
            float x = m[i], a = MathF.Abs(x);
            if (a > 0.7f) x = MathF.CopySign(0.7f + 0.3f * MathF.Tanh((a - 0.7f) / 0.3f), x);
            m[i] = x;
        }
        Meter.Push(m);
        for (int f = 0; f < frames; f++)
        {
            int o = f * _ch;
            for (int c = 0; c < _ch; c++) dst[o + c] = m[f];
        }
        return count;
    }

    public void Dispose()
    {
        try { _out.Stop(); } catch { }
        _out.Dispose();
    }
}

/// <summary>Base for anything that produces mono audio and pushes it to taps.</summary>
public abstract class Source : IDisposable
{
    internal readonly List<Tap> Taps = new();
    /// <summary>Taps that get the audio before this source's gain and mute (the headset monitor).</summary>
    internal readonly List<Tap> RawTaps = new();
    float _gain = 1;
    int _muted;
    float[] _scratch = new float[8192];

    public Meter Meter { get; } = new();
    public int SampleRate { get; protected set; } = 48000;
    public string Status { get; protected set; } = "";
    public long Packets { get; protected set; }
    public event Action<string> Faulted;

    public float GainDb { set => Volatile.Write(ref _gain, MathF.Pow(10, value / 20)); }
    public bool Muted
    {
        get => Volatile.Read(ref _muted) == 1;
        set => Volatile.Write(ref _muted, value ? 1 : 0);
    }

    public abstract void Start();
    public abstract void Dispose();

    protected void Fault(string msg) => Faulted?.Invoke(msg);

    protected void Emit(ReadOnlySpan<float> mono)
    {
        Packets++;
        foreach (var t in RawTaps) t.Write(mono);
        if (_scratch.Length < mono.Length) _scratch = new float[mono.Length * 2];
        var s = _scratch.AsSpan(0, mono.Length);
        float g = Muted ? 0 : Volatile.Read(ref _gain);
        for (int i = 0; i < s.Length; i++) s[i] = mono[i] * g;
        Meter.Push(s);
        foreach (var t in Taps) t.Write(s);
    }

    static readonly Guid FloatSubType = new("00000003-0000-0010-8000-00aa00389b71");

    /// <summary>Downmixes interleaved PCM/float of any common format to mono floats.</summary>
    protected static int ToMono(ReadOnlySpan<byte> data, WaveFormat fmt, ref float[] buf)
    {
        int ch = fmt.Channels, frames = data.Length / fmt.BlockAlign;
        if (buf.Length < frames) buf = new float[frames * 2];
        float inv = 1f / ch;
        bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat ||
                       (fmt is WaveFormatExtensible ext && ext.SubFormat == FloatSubType && fmt.BitsPerSample == 32);
        if (isFloat)
        {
            var src = MemoryMarshal.Cast<byte, float>(data[..(frames * fmt.BlockAlign)]);
            for (int f = 0; f < frames; f++) { float s = 0; int o = f * ch; for (int c = 0; c < ch; c++) s += src[o + c]; buf[f] = s * inv; }
        }
        else if (fmt.BitsPerSample == 16)
        {
            var src = MemoryMarshal.Cast<byte, short>(data[..(frames * fmt.BlockAlign)]);
            for (int f = 0; f < frames; f++) { float s = 0; int o = f * ch; for (int c = 0; c < ch; c++) s += src[o + c] / 32768f; buf[f] = s * inv; }
        }
        else if (fmt.BitsPerSample == 24)
        {
            for (int f = 0; f < frames; f++)
            {
                float s = 0;
                for (int c = 0; c < ch; c++)
                {
                    int o = (f * ch + c) * 3;
                    s += (data[o] | (data[o + 1] << 8) | ((sbyte)data[o + 2] << 16)) / 8388608f;
                }
                buf[f] = s * inv;
            }
        }
        else
        {
            var src = MemoryMarshal.Cast<byte, int>(data[..(frames * fmt.BlockAlign)]);
            for (int f = 0; f < frames; f++) { float s = 0; int o = f * ch; for (int c = 0; c < ch; c++) s += src[o + c] / 2147483648f; buf[f] = s * inv; }
        }
        return frames;
    }
}

/// <summary>A real microphone (any WASAPI capture device).</summary>
public sealed class MicSource : Source
{
    readonly WasapiCapture _cap;
    float[] _mono = new float[8192];

    public MicSource(MMDevice dev, int latencyMs)
    {
        _cap = new WasapiCapture(dev, true, latencyMs);
        SampleRate = _cap.WaveFormat.SampleRate;
        _cap.DataAvailable += (_, e) =>
        {
            int n = ToMono(e.Buffer.AsSpan(0, e.BytesRecorded), _cap.WaveFormat, ref _mono);
            if (n > 0) Emit(_mono.AsSpan(0, n));
        };
        _cap.RecordingStopped += (_, e) => { if (e.Exception != null) Fault(e.Exception.Message); };
    }

    public override void Start() { _cap.StartRecording(); Status = "live"; }

    public override void Dispose()
    {
        try { _cap.StopRecording(); } catch { }
        _cap.Dispose();
    }
}

/// <summary>
/// Captures only what one app plays (Discord, the Xbox party chat host...), whatever device it plays on.
/// Finds the app by process name and re-attaches by itself if the app restarts.
/// </summary>
public sealed class AppSource : Source
{
    const int Rate = 48000;
    readonly string _processName;
    readonly Thread _thread;
    volatile bool _run = true;
    float[] _mono = new float[8192];

    public AppSource(string processName)
    {
        _processName = processName;
        SampleRate = Rate;
        Status = "waiting";
        _thread = new Thread(Loop) { IsBackground = true, Name = "AppSource " + processName, Priority = ThreadPriority.AboveNormal };
        _thread.SetApartmentState(ApartmentState.MTA);
    }

    public int Pid { get; private set; } = -1;

    public override void Start() => _thread.Start();

    void Loop()
    {
        while (_run)
        {
            int pid = Procs.FindRoot(_processName);
            if (pid <= 0)
            {
                Pid = -1;
                Status = "app not running";
                Sleep(1000);
                continue;
            }
            Pid = pid;
            try
            {
                Capture(pid);
            }
            catch (Exception ex)
            {
                Status = "error: " + ex.Message;
                Fault($"{_processName}: {ex.Message}");
                Sleep(2000);
            }
        }
    }

    void Sleep(int ms)
    {
        for (int t = 0; t < ms && _run; t += 50) Thread.Sleep(50);
    }

    void Capture(int pid)
    {
        var client = ProcessLoopbackApi.Activate(pid);
        try
        {
            uint flags = ProcessLoopbackApi.StreamFlagsLoopback | ProcessLoopbackApi.StreamFlagsAutoConvertPcm | ProcessLoopbackApi.StreamFlagsSrcDefaultQuality;
            var wfx = WaveFormatEx.Create(3, Rate, 2, 32);
            var fmt = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2);
            int hr = client.Initialize(0, flags, 2_000_000, 0, ref wfx, IntPtr.Zero);
            if (hr < 0)
            {
                // Some builds only take integer PCM here.
                Marshal.ReleaseComObject(client);
                client = ProcessLoopbackApi.Activate(pid);
                wfx = WaveFormatEx.Create(1, Rate, 2, 16);
                fmt = new WaveFormat(Rate, 16, 2);
                Marshal.ThrowExceptionForHR(client.Initialize(0, flags, 2_000_000, 0, ref wfx, IntPtr.Zero));
            }
            var iid = ProcessLoopbackApi.IID_IAudioCaptureClient;
            Marshal.ThrowExceptionForHR(client.GetService(ref iid, out object svc));
            var cap = (IAudioCaptureClientRaw)svc;
            Marshal.ThrowExceptionForHR(client.Start());
            Status = $"live (pid {pid})";
            byte[] bytes = new byte[0];
            var lastCheck = Environment.TickCount64;
            try
            {
                while (_run)
                {
                    Thread.Sleep(5);
                    while (true)
                    {
                        Marshal.ThrowExceptionForHR(cap.GetNextPacketSize(out uint next));
                        if (next == 0) break;
                        Marshal.ThrowExceptionForHR(cap.GetBuffer(out IntPtr data, out uint frames, out uint bflags, out _, out _));
                        int len = (int)frames * fmt.BlockAlign;
                        if (bytes.Length < len) bytes = new byte[len * 2];
                        if ((bflags & ProcessLoopbackApi.BufferFlagsSilent) != 0) Array.Clear(bytes, 0, len);
                        else Marshal.Copy(data, bytes, 0, len);
                        cap.ReleaseBuffer(frames);
                        int n = ToMono(bytes.AsSpan(0, len), fmt, ref _mono);
                        if (n > 0) Emit(_mono.AsSpan(0, n));
                    }
                    if (Environment.TickCount64 - lastCheck > 1000)
                    {
                        lastCheck = Environment.TickCount64;
                        if (!Procs.IsAlive(pid)) { Status = "app closed, waiting"; return; }
                    }
                }
            }
            finally
            {
                client.Stop();
                Marshal.ReleaseComObject(cap);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(client);
        }
    }

    public override void Dispose()
    {
        _run = false;
        _thread.Join(3000);
    }
}

/// <summary>
/// Records everything played on one output device. Used for the Xbox party, whose chat stream Windows
/// keeps out of per-app capture: the party plays into an otherwise unused device and we record that.
/// </summary>
public sealed class DeviceLoopbackSource : Source
{
    readonly WasapiLoopbackCapture _cap;
    float[] _mono = new float[8192];

    public DeviceLoopbackSource(MMDevice dev)
    {
        _cap = new WasapiLoopbackCapture(dev);
        SampleRate = _cap.WaveFormat.SampleRate;
        Status = "listening to " + dev.FriendlyName;
        _cap.DataAvailable += (_, e) =>
        {
            int n = ToMono(e.Buffer.AsSpan(0, e.BytesRecorded), _cap.WaveFormat, ref _mono);
            if (n > 0) Emit(_mono.AsSpan(0, n));
        };
        _cap.RecordingStopped += (_, e) => { if (e.Exception != null) Fault(e.Exception.Message); };
    }

    public override void Start() => _cap.StartRecording();

    public override void Dispose()
    {
        try { _cap.StopRecording(); } catch { }
        _cap.Dispose();
    }
}

public sealed class BridgeConfig
{
    public string MicId { get; set; }
    public string ToXboxId { get; set; }
    public string ToDiscordId { get; set; }
    public string DiscordApp { get; set; } = "Discord";
    public string XboxApp { get; set; } = "XboxPartyChatHost";
    /// <summary>Output device the Xbox party plays into and we record. Empty = per-app capture of XboxApp.</summary>
    public string XboxSpeakerId { get; set; }
    /// <summary>Where you hear the Xbox party when it plays into XboxSpeakerId (your headset).</summary>
    public string HearXboxOnId { get; set; }
    public int LatencyMs { get; set; } = 20;
}

/// <summary>
/// The two-way bridge:
///   mic + Discord app audio  -> "to Xbox" output   (Xbox party uses its matching capture side as mic)
///   mic + Xbox party audio   -> "to Discord" output (Discord uses its matching capture side as mic)
/// Neither app's own output is ever sent back to itself, so there is no feedback loop.
/// </summary>
public sealed class Bridge : IDisposable
{
    readonly MMDeviceEnumerator _enum = new();
    Bus _toXbox, _toDiscord, _monitor;

    public MicSource Mic { get; private set; }
    public AppSource Discord { get; private set; }
    public Source Xbox { get; private set; }
    public Meter ToXboxMeter => _toXbox?.Meter;
    public Meter ToDiscordMeter => _toDiscord?.Meter;
    public bool IsRunning { get; private set; }
    public int Underruns => (_toXbox?.Underruns ?? 0) + (_toDiscord?.Underruns ?? 0);
    public event Action<string> Faulted;

    public List<DeviceInfo> GetDevices(DataFlow flow) =>
        _enum.EnumerateAudioEndPoints(flow, DeviceState.Active)
             .Select(d => new DeviceInfo(d.ID, d.FriendlyName))
             .OrderBy(d => d.Name).ToList();

    public string DefaultId(DataFlow flow)
    {
        try { return _enum.GetDefaultAudioEndpoint(flow, Role.Console).ID; }
        catch { return null; }
    }

    /// <summary>Starts the bridge. Returns notes about anything it adjusted.</summary>
    public List<string> Start(BridgeConfig c)
    {
        Stop();
        if (c.ToXboxId == c.ToDiscordId) throw new InvalidOperationException("Both outputs are the same device. Each side needs its own virtual cable.");
        var micName = _enum.GetDevice(c.MicId).FriendlyName;
        foreach (var outId in new[] { c.ToXboxId, c.ToDiscordId })
            if (PairedCapture(outId)?.ID == c.MicId)
                throw new InvalidOperationException($"Your mic is set to {micName}, which is one of the bridge's own cables. Pick your real mic.");
        bool deviceMode = !string.IsNullOrEmpty(c.XboxSpeakerId);
        if (deviceMode)
        {
            if (c.XboxSpeakerId == c.ToXboxId || c.XboxSpeakerId == c.ToDiscordId)
                throw new InvalidOperationException("The Xbox party speaker can't be one of the bridge's own cables. Use an unused device like Steam Streaming Speakers.");
            if (c.XboxSpeakerId == c.HearXboxOnId)
                throw new InvalidOperationException("The Xbox party speaker and your headset are the same device. Point the party at an unused device like Steam Streaming Speakers.");
        }
        var notes = new List<string>();
        if (System.Diagnostics.Process.GetProcessesByName("vrserver").Length > 0)
            notes.Add("SteamVR is running, and it uses the Steam Streaming devices for your headset, so the bridge shares them with VR.");
        foreach (var outId in new[] { c.ToXboxId, c.ToDiscordId })
        {
            var note = LevelPairedCapture(outId);
            if (note != null) notes.Add(note);
        }
        try
        {
            _toXbox = new Bus(_enum.GetDevice(c.ToXboxId), c.LatencyMs);
            _toDiscord = new Bus(_enum.GetDevice(c.ToDiscordId), c.LatencyMs);
            _toXbox.Faulted += m => Faulted?.Invoke("To Xbox output: " + m);
            _toDiscord.Faulted += m => Faulted?.Invoke("To Discord output: " + m);

            Mic = new MicSource(_enum.GetDevice(c.MicId), c.LatencyMs);
            Discord = new AppSource(c.DiscordApp);
            if (deviceMode) notes.Add(RouteXbox(c));
            Xbox = deviceMode ? new DeviceLoopbackSource(_enum.GetDevice(c.XboxSpeakerId)) : new AppSource(c.XboxApp);
            if (deviceMode && !string.IsNullOrEmpty(c.HearXboxOnId))
            {
                _monitor = new Bus(_enum.GetDevice(c.HearXboxOnId), Math.Max(c.LatencyMs, 30));
                _monitor.Faulted += m => Faulted?.Invoke("Headset output: " + m);
                Xbox.RawTaps.Add(_monitor.AddTap(Xbox.SampleRate));
            }
            foreach (var s in new Source[] { Mic, Discord, Xbox }) s.Faulted += m => Faulted?.Invoke(m);

            Mic.Taps.Add(_toXbox.AddTap(Mic.SampleRate));
            Mic.Taps.Add(_toDiscord.AddTap(Mic.SampleRate));
            Discord.Taps.Add(_toXbox.AddTap(Discord.SampleRate));
            Xbox.Taps.Add(_toDiscord.AddTap(Xbox.SampleRate));

            _toXbox.Start();
            _toDiscord.Start();
            _monitor?.Start();
            Mic.Start();
            Discord.Start();
            Xbox.Start();
            IsRunning = true;
            return notes;
        }
        catch
        {
            Stop();
            throw;
        }
    }

    /// <summary>The recording side of a virtual output (CABLE Input -> CABLE Output, Speakers (X) -> Microphone (X)).</summary>
    public MMDevice PairedCapture(string renderId)
    {
        try
        {
            string want = Defaults.CaptureSideOf(_enum.GetDevice(renderId).FriendlyName);
            return _enum.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).FirstOrDefault(d => d.FriendlyName == want);
        }
        catch { return null; }
    }

    /// <summary>
    /// Virtual mics sometimes ship with a boosted recording level (Steam Streaming Microphone defaults to +30 dB),
    /// which would make the relayed voices far too loud. Puts the level back at 0 dB, unmuted.
    /// </summary>
    string LevelPairedCapture(string renderId)
    {
        try
        {
            var cap = PairedCapture(renderId);
            if (cap == null) return null;
            var v = cap.AudioEndpointVolume;
            float db = v.MasterVolumeLevel;
            bool fixedMute = v.Mute;
            bool needLevel = !(Math.Abs(db) < 0.5f || v.VolumeRange.MinDecibels > 0 || v.VolumeRange.MaxDecibels < 0);
            if (needLevel || fixedMute)
                Restore.Remember(new Restore.Entry { Kind = "level", DeviceId = cap.ID, Db = db, Muted = fixedMute });
            if (fixedMute) v.Mute = false;
            if (!needLevel)
                return fixedMute ? $"Unmuted {cap.FriendlyName}." : null;
            v.MasterVolumeLevel = 0;
            return $"Set {cap.FriendlyName} recording level from {db:+0;-0} dB to 0 dB.";
        }
        catch { return null; }
    }

    /// <summary>Points the party chat app's output at the Xbox speaker device (undone in Stop).</summary>
    string RouteXbox(BridgeConfig c)
    {
        int pid = Procs.FindRoot(c.XboxApp);
        if (pid <= 0) return $"{c.XboxApp} isn't running, so its speaker wasn't switched. Join the party, then press START again.";
        try
        {
            string original = AppRouting.GetOutput(pid);
            Restore.Remember(new Restore.Entry
            {
                Kind = "route",
                DeviceId = original == c.XboxSpeakerId ? "" : original,
                Apps = new List<string> { c.XboxApp, "XboxPcApp" },
            });
            AppRouting.SetOutput(pid, c.XboxSpeakerId);
            return "Party speaker switched to " + _enum.GetDevice(c.XboxSpeakerId).FriendlyName + ".";
        }
        catch (Exception ex)
        {
            return "Couldn't switch the party speaker (" + ex.Message + "). Set it by hand.";
        }
    }

    public void Stop()
    {
        IsRunning = false;
        Restore.ApplyAll();
        Mic?.Dispose(); Discord?.Dispose(); Xbox?.Dispose();
        _toXbox?.Dispose(); _toDiscord?.Dispose(); _monitor?.Dispose();
        Mic = null; Discord = null; Xbox = null; _toXbox = null; _toDiscord = null; _monitor = null;
    }

    public void Dispose() => Stop();
}
