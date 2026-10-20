using System.Runtime.InteropServices;

namespace PartyBridge.Audio;

// Minimal Core Audio interop for per-process loopback capture
// (ActivateAudioInterfaceAsync + VAD\Process_Loopback, Windows 10 2004 / build 20348 and later).

[StructLayout(LayoutKind.Sequential, Pack = 2)]
struct WaveFormatEx
{
    public ushort FormatTag;
    public ushort Channels;
    public uint SamplesPerSec;
    public uint AvgBytesPerSec;
    public ushort BlockAlign;
    public ushort BitsPerSample;
    public ushort Size;

    public static WaveFormatEx Create(ushort tag, int rate, int channels, int bits) => new()
    {
        FormatTag = tag,
        Channels = (ushort)channels,
        SamplesPerSec = (uint)rate,
        BitsPerSample = (ushort)bits,
        BlockAlign = (ushort)(channels * bits / 8),
        AvgBytesPerSec = (uint)(rate * channels * bits / 8),
    };
}

[ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioClientRaw
{
    [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, [In] ref WaveFormatEx format, IntPtr sessionGuid);
    [PreserveSig] int GetBufferSize(out uint frames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint padding);
    [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, IntPtr closest);
    [PreserveSig] int GetMixFormat(out IntPtr format);
    [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minPeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr handle);
    [PreserveSig] int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
}

[ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioCaptureClientRaw
{
    [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
    [PreserveSig] int ReleaseBuffer(uint frames);
    [PreserveSig] int GetNextPacketSize(out uint frames);
}

[ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IActivateAudioInterfaceAsyncOperation
{
    [PreserveSig] int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
}

[ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IActivateAudioInterfaceCompletionHandler
{
    [PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
}

// Marker interface; ActivateAudioInterfaceAsync rejects completion handlers that are not agile.
[ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAgileObject { }

sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
{
    public readonly ManualResetEventSlim Done = new();
    public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
    {
        Done.Set();
        return 0;
    }
}

static class ProcessLoopbackApi
{
    const string VirtualDeviceProcessLoopback = @"VAD\Process_Loopback";
    static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    public static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    public const uint StreamFlagsLoopback = 0x00020000;
    public const uint StreamFlagsAutoConvertPcm = 0x80000000;
    public const uint StreamFlagsSrcDefaultQuality = 0x08000000;
    public const uint BufferFlagsSilent = 0x2;

    [DllImport("Mmdevapi.dll", ExactSpelling = true)]
    static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    /// <summary>Activates an IAudioClient that captures everything the process tree under <paramref name="pid"/> plays.</summary>
    public static IAudioClientRaw Activate(int pid)
    {
        // AUDIOCLIENT_ACTIVATION_PARAMS { ActivationType = PROCESS_LOOPBACK (1), { TargetProcessId, INCLUDE_TARGET_PROCESS_TREE (0) } }
        IntPtr ap = Marshal.AllocHGlobal(12);
        // PROPVARIANT (24 bytes on x64): vt = VT_BLOB (65), blob.cbSize at 8, blob.pBlobData at 16.
        IntPtr pv = Marshal.AllocHGlobal(24);
        try
        {
            Marshal.WriteInt32(ap, 0, 1);
            Marshal.WriteInt32(ap, 4, pid);
            Marshal.WriteInt32(ap, 8, 0);
            for (int i = 0; i < 24; i += 4) Marshal.WriteInt32(pv, i, 0);
            Marshal.WriteInt16(pv, 0, 65);
            Marshal.WriteInt32(pv, 8, 12);
            Marshal.WriteIntPtr(pv, IntPtr.Size == 8 ? 16 : 12, ap);

            var handler = new ActivationHandler();
            var iid = IID_IAudioClient;
            int hr = ActivateAudioInterfaceAsync(VirtualDeviceProcessLoopback, ref iid, pv, handler, out var op);
            Marshal.ThrowExceptionForHR(hr);
            if (!handler.Done.Wait(5000)) throw new TimeoutException("Process loopback activation timed out");
            Marshal.ThrowExceptionForHR(op.GetActivateResult(out int result, out object iface));
            Marshal.ThrowExceptionForHR(result);
            return (IAudioClientRaw)iface;
        }
        finally
        {
            Marshal.FreeHGlobal(pv);
            Marshal.FreeHGlobal(ap);
        }
    }
}
