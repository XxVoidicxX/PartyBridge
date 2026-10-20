using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using PartyBridge.Audio;

namespace PartyBridge;

public partial class MainWindow : Window
{
    readonly Bridge _bridge = new();
    readonly Settings _s = Settings.Load();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    readonly string _snapshot;
    bool _loading = true;

    readonly bool _startNow;

    public MainWindow(string snapshot = null, bool startNow = false)
    {
        _snapshot = snapshot;
        _startNow = startNow;
        InitializeComponent();
        // Undo anything a previous run left behind (killed, crashed, or the PC restarted while it ran).
        var undone = _snapshot == null ? Restore.ApplyAll() : new List<string>();
        _bridge.Faulted += m => Dispatcher.BeginInvoke(() => SetStatus(m));

        FillLists();
        MicGain.Value = _s.MicGainDb;
        DiscordGain.Value = _s.DiscordGainDb;
        XboxGain.Value = _s.XboxGainDb;
        MuteToggle.IsChecked = _s.MicMuted;
        AutoStartCheck.IsChecked = _s.AutoStart;
        UpdateGainTexts();
        UpdateHints();

        StartBtn.Click += (_, _) => { if (_bridge.IsRunning) StopBridge(); else StartBridge(); };
        RefreshBtn.Click += (_, _) => FillLists();
        MuteToggle.Click += (_, _) => ApplyLive();
        PauseToggle.Click += (_, _) => ApplyLive();
        AutoStartCheck.Click += (_, _) => { _s.AutoStart = AutoStartCheck.IsChecked == true; _s.Save(); };

        foreach (var sl in new[] { MicGain, DiscordGain, XboxGain })
        {
            sl.ValueChanged += (_, _) => { UpdateGainTexts(); ApplyLive(); };
            sl.MouseDoubleClick += (o, _) => ((Slider)o).Value = 0;
        }
        foreach (var cb in new[] { MicBox, ToXboxBox, ToDiscordBox, XboxSpeakerBox, HearBox })
            cb.SelectionChanged += (_, _) => { UpdateHints(); RestartIfRunning(); };
        foreach (var cb in new[] { DiscordAppBox, XboxAppBox })
        {
            cb.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(RestartIfRunning);
            cb.LostKeyboardFocus += (_, _) => RestartIfRunning();
        }

        if (undone.Count > 0) SetStatus("Cleaned up after the last run: " + string.Join(", ", undone) + ".");
        Application.Current.SessionEnding += (_, _) => _bridge.Stop();
        _timer.Tick += (_, _) => Tick();
        Loaded += (_, _) =>
        {
            _loading = false;
            if (_snapshot != null) { Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, TakeSnapshot); return; }
            _timer.Start();
            if (_s.AutoStart || _startNow) StartBridge();
        };
        Closing += (_, _) => { SaveSettings(); _bridge.Dispose(); };
    }

    void FillLists()
    {
        bool was = _loading;
        _loading = true;
        var cfg = Defaults.Build(_bridge, _s.Bridge);
        Fill(MicBox, _bridge.GetDevices(DataFlow.Capture), cfg.MicId);
        var rends = _bridge.GetDevices(DataFlow.Render);
        Fill(ToXboxBox, rends, cfg.ToXboxId);
        Fill(ToDiscordBox, rends, cfg.ToDiscordId);
        Fill(XboxSpeakerBox, new[] { new DeviceInfo("", "None: capture the Xbox app directly") }.Concat(rends).ToList(), cfg.XboxSpeakerId ?? "");
        Fill(HearBox, rends, cfg.HearXboxOnId);

        var apps = Procs.AudioSessionApps();
        foreach (var must in new[] { "Discord", "XboxPartyChatHost" })
            if (!apps.Contains(must, StringComparer.OrdinalIgnoreCase)) apps.Add(must);
        apps.Sort(StringComparer.OrdinalIgnoreCase);
        DiscordAppBox.ItemsSource = apps;
        XboxAppBox.ItemsSource = apps.ToList();
        DiscordAppBox.Text = string.IsNullOrWhiteSpace(cfg.DiscordApp) ? "Discord" : cfg.DiscordApp;
        XboxAppBox.Text = string.IsNullOrWhiteSpace(cfg.XboxApp) ? "XboxPartyChatHost" : cfg.XboxApp;
        _loading = was;
        UpdateHints();
    }

    static void Fill(ComboBox box, List<DeviceInfo> items, string selectId)
    {
        box.ItemsSource = items;
        box.SelectedItem = items.FirstOrDefault(d => d.Id == selectId);
    }

    BridgeConfig ReadConfig() => new()
    {
        MicId = (MicBox.SelectedItem as DeviceInfo)?.Id,
        ToXboxId = (ToXboxBox.SelectedItem as DeviceInfo)?.Id,
        ToDiscordId = (ToDiscordBox.SelectedItem as DeviceInfo)?.Id,
        DiscordApp = DiscordAppBox.Text.Trim(),
        XboxApp = XboxAppBox.Text.Trim(),
        XboxSpeakerId = (XboxSpeakerBox.SelectedItem as DeviceInfo)?.Id ?? "",
        HearXboxOnId = (HearBox.SelectedItem as DeviceInfo)?.Id,
        LatencyMs = _s.Bridge.LatencyMs is >= 10 and <= 200 ? _s.Bridge.LatencyMs : 20,
    };

    void StartBridge()
    {
        var c = ReadConfig();
        if (c.MicId == null || c.ToXboxId == null || c.ToDiscordId == null)
        {
            SetStatus("Pick your mic and both virtual outputs first.");
            return;
        }
        try
        {
            var notes = _bridge.Start(c);
            ApplyLive();
            StartBtn.Content = "STOP";
            SetStatus(notes.Count > 0 ? "Bridge running. " + string.Join(" ", notes) : "Bridge running.");
            SaveSettings();
        }
        catch (Exception ex)
        {
            StartBtn.Content = "START";
            SetStatus("Could not start: " + ex.Message);
        }
    }

    void StopBridge()
    {
        _bridge.Stop();
        StartBtn.Content = "START";
        DiscordStatus.Text = XboxStatus.Text = "stopped";
        SetStatus("Stopped.");
    }

    void RestartIfRunning()
    {
        if (_loading) return;
        SaveSettings();
        if (_bridge.IsRunning) StartBridge();
    }

    void ApplyLive()
    {
        if (_bridge.Mic != null)
        {
            _bridge.Mic.GainDb = (float)MicGain.Value;
            _bridge.Mic.Muted = MuteToggle.IsChecked == true;
            _bridge.Discord.GainDb = (float)DiscordGain.Value;
            _bridge.Xbox.GainDb = (float)XboxGain.Value;
            bool paused = PauseToggle.IsChecked == true;
            _bridge.Discord.Muted = paused;
            _bridge.Xbox.Muted = paused;
        }
        if (!_loading) SaveSettings();
    }

    void SaveSettings()
    {
        _s.Bridge = ReadConfig();
        _s.MicGainDb = (float)MicGain.Value;
        _s.DiscordGainDb = (float)DiscordGain.Value;
        _s.XboxGainDb = (float)XboxGain.Value;
        _s.MicMuted = MuteToggle.IsChecked == true;
        _s.AutoStart = AutoStartCheck.IsChecked == true;
        _s.Save();
    }

    void UpdateGainTexts()
    {
        static string Db(double v) => v == 0 ? "0 dB" : $"{v:+0;-0} dB";
        MicGainText.Text = Db(MicGain.Value);
        DiscordGainText.Text = Db(DiscordGain.Value);
        XboxGainText.Text = Db(XboxGain.Value);
    }

    void UpdateHints()
    {
        string d = Defaults.CaptureSideOf((ToDiscordBox.SelectedItem as DeviceInfo)?.Name);
        string x = Defaults.CaptureSideOf((ToXboxBox.SelectedItem as DeviceInfo)?.Name);
        DiscordHint.Text = string.IsNullOrEmpty(d) ? "Pick a virtual output." : $"In Discord, set Input Device to:\n{d}";
        XboxHint.Text = string.IsNullOrEmpty(x) ? "Pick a virtual output." : $"In the Xbox app party, set the mic to:\n{x}";
        var spk = XboxSpeakerBox.SelectedItem as DeviceInfo;
        bool deviceMode = !string.IsNullOrEmpty(spk?.Id);
        HearBox.IsEnabled = deviceMode;
        XboxSpeakerHint.Text = deviceMode ? $"START switches the party's speaker to:\n{spk.Name}" : "";
        if (!deviceMode) XboxSpeakerCheck.Text = "";
    }

    // Peak meters with a quick attack and a slow fall, mapped from -60..0 dBFS.
    readonly Dictionary<ProgressBar, double> _meterVals = new();

    void ShowMeter(ProgressBar bar, Meter m)
    {
        double target = 0;
        if (m != null)
        {
            float p = m.Take();
            target = p <= 1e-6f ? 0 : Math.Clamp((20 * Math.Log10(p) + 60) / 60, 0, 1);
        }
        _meterVals.TryGetValue(bar, out double cur);
        cur = target > cur ? target : Math.Max(0, cur - 0.025);
        _meterVals[bar] = cur;
        bar.Value = cur;
    }

    // Which mic each app is really recording from, refreshed off the UI thread.
    long _lastCheck;
    bool _checking;

    void RefreshChecks()
    {
        if (_checking || Environment.TickCount64 - _lastCheck < 1500) return;
        _checking = true;
        _lastCheck = Environment.TickCount64;
        var c = ReadConfig();
        string toDiscordCap = Defaults.CaptureSideOf((ToDiscordBox.SelectedItem as DeviceInfo)?.Name);
        string toXboxCap = Defaults.CaptureSideOf((ToXboxBox.SelectedItem as DeviceInfo)?.Name);
        string speaker = string.IsNullOrEmpty(c.XboxSpeakerId) ? null : (XboxSpeakerBox.SelectedItem as DeviceInfo)?.Name;
        Task.Run(() =>
        {
            var plays = speaker == null ? ((string, Brush)?)null : SpeakerCheck(CaptureSessions.List(DataFlow.Render), c.XboxApp, speaker);
            var uses = CaptureSessions.List();
            var d = Check(uses, c.DiscordApp, "Discord", toDiscordCap, toXboxCap, "Xbox");
            var x = Check(uses, c.XboxApp, "The Xbox party", toXboxCap, toDiscordCap, "Discord");
            Dispatcher.BeginInvoke(() =>
            {
                (DiscordCheck.Text, DiscordCheck.Foreground) = d;
                (XboxCheck.Text, XboxCheck.Foreground) = x;
                if (plays != null) (XboxSpeakerCheck.Text, XboxSpeakerCheck.Foreground) = plays.Value;
                _checking = false;
            });
        });
    }

    static readonly Brush Good = new SolidColorBrush(Color.FromRgb(0x4C, 0xC9, 0x6B));
    static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xE0, 0xA3, 0x3A));
    static readonly Brush Bad = new SolidColorBrush(Color.FromRgb(0xF0, 0x5A, 0x5A));

    static (string, Brush) SpeakerCheck(List<CaptureUse> outs, string app, string want)
    {
        var active = outs.Where(u => u.Active && string.Equals(u.ProcessName, app, StringComparison.OrdinalIgnoreCase)).ToList();
        if (active.Count == 0) return ("The Xbox party isn't playing sound right now.", Warn);
        if (active.Any(u => u.DeviceName == want)) return ("OK: the party plays into the bridge.", Good);
        return ($"The party still plays on {active[0].DeviceName}. Set its speaker to {want}.", Warn);
    }

    static (string, Brush) Check(List<CaptureUse> uses, string app, string label, string want, string otherSide, string otherLabel)
    {
        if (string.IsNullOrEmpty(want)) return ("", Warn);
        var mine = uses.Where(u => string.Equals(u.ProcessName, app, StringComparison.OrdinalIgnoreCase)).ToList();
        var active = mine.Where(u => u.Active).ToList();
        if (active.Count == 0)
            return (mine.Count == 0 ? $"{label} is not using any mic yet." : $"{label} has no mic open right now.", Warn);
        if (active.Any(u => u.DeviceName == want))
            return ($"OK: {label} is listening to the bridge.", Good);
        if (active.Any(u => u.DeviceName == otherSide))
            return ($"Echo loop: {label} is listening to the {otherLabel} cable. Switch it to {want}.", Bad);
        return ($"{label} is still using {active[0].DeviceName}. Switch it to {want}.", Warn);
    }

    void Tick()
    {
        RefreshChecks();
        ShowMeter(MicMeter, _bridge.Mic?.Meter);
        ShowMeter(DiscordMeter, _bridge.Discord?.Meter);
        ShowMeter(XboxMeter, _bridge.Xbox?.Meter);
        ShowMeter(ToDiscordMeter, _bridge.ToDiscordMeter);
        ShowMeter(ToXboxMeter, _bridge.ToXboxMeter);
        if (_bridge.IsRunning)
        {
            DiscordStatus.Text = _bridge.Discord.Status;
            XboxStatus.Text = _bridge.Xbox.Status;
        }
    }

    void SetStatus(string s) => StatusText.Text = s;

    // Global hotkeys: Ctrl+Alt+N mute my mic, Ctrl+Alt+P pause relay.
    const int HkMute = 1, HkPause = 2, WmHotkey = 0x0312;
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        const uint mods = 0x1 | 0x2 | 0x4000; // Alt + Ctrl, no auto-repeat
        bool ok = RegisterHotKey(hwnd, HkMute, mods, 0x4E) & RegisterHotKey(hwnd, HkPause, mods, 0x50);
        if (!ok) Dispatcher.BeginInvoke(() => SetStatus("A hotkey is taken by another app. The buttons still work."));
    }

    protected override void OnClosed(EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        UnregisterHotKey(hwnd, HkMute);
        UnregisterHotKey(hwnd, HkPause);
        base.OnClosed(e);
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey) return IntPtr.Zero;
        ToggleButton t = wParam.ToInt32() switch { HkMute => MuteToggle, HkPause => PauseToggle, _ => null };
        if (t != null)
        {
            t.IsChecked = t.IsChecked != true;
            ApplyLive();
            SetStatus(t == MuteToggle ? (t.IsChecked == true ? "Your mic is muted." : "Your mic is live.")
                                      : (t.IsChecked == true ? "Relay paused." : "Relay on."));
            handled = true;
        }
        return IntPtr.Zero;
    }

    void TakeSnapshot()
    {
        MicMeter.Value = 0.62; DiscordMeter.Value = 0.48; XboxMeter.Value = 0.71;
        ToDiscordMeter.Value = 0.74; ToXboxMeter.Value = 0.66;
        DiscordStatus.Text = "live (pid 17236)"; XboxStatus.Text = "live (pid 9244)";
        StartBtn.Content = "STOP";
        UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(this);
        var root = (FrameworkElement)Content;
        var rtb = new RenderTargetBitmap((int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY),
                                         dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        rtb.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var f = File.Create(_snapshot)) enc.Save(f);
        Close();
    }
}
