using System.Windows;
using NAudio.Wave;

namespace PartyBridge;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        if (args.Length >= 2 && args[0] == "--selftest")
        {
            try { SelfTest.Run(args[1], args.Length >= 3 && args[2] == "tones"); }
            catch (Exception ex) { File.AppendAllText(args[1], "\nCRASH: " + ex); }
            Shutdown(0);
            return;
        }
        if (args.Length >= 2 && args[0] == "--diag") { try { Diag.Run(args[1], args.Length >= 3 ? int.Parse(args[2]) : 8); } catch (Exception ex) { File.WriteAllText(args[1], ex.ToString()); } Shutdown(0); return; }
        if (args.Length >= 2 && args[0] == "--restore")
        {
            try { File.WriteAllLines(args[1], PartyBridge.Audio.Restore.ApplyAll()); } catch (Exception ex) { File.WriteAllText(args[1], ex.ToString()); }
            Shutdown(0);
            return;
        }
        if (args.Length >= 2 && args[0] == "--probe") { Probe.Run(args[1], args[2..]); Shutdown(0); return; }
        if (args.Length >= 3 && args[0] == "--e2e") { try { E2E.Run(args[1], args[2]); } catch (Exception ex) { File.WriteAllText(args[1], ex.ToString()); } Shutdown(0); return; }
        if (args.Length >= 1 && args[0] == "--tone")
        {
            // Test helper: 6 s of 1 kHz at -20 dBFS into a device nobody listens to.
            using var en = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            var dev = en.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active).First(d => d.FriendlyName.Contains("Steam Streaming Speakers"));
            using var wo = new NAudio.Wave.WasapiOut(dev, NAudio.CoreAudioApi.AudioClientShareMode.Shared, true, 40);
            wo.Init(new NAudio.Wave.SampleProviders.SignalGenerator(48000, 2) { Frequency = 1000, Gain = 0.1 }.ToWaveProvider());
            wo.Play();
            Thread.Sleep(6000);
            Shutdown(0);
            return;
        }
        string snapshot = args.Length >= 2 && args[0] == "--snapshot" ? args[1] : null;
        new MainWindow(snapshot, args.Contains("--start")).Show();
    }
}
