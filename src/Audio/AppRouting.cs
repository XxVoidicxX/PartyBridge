using System.Runtime.InteropServices;

namespace PartyBridge.Audio;

/// <summary>
/// Per-app output device, the same setting as Windows Settings > Sound > Volume mixer > app > Output device.
/// Uses the undocumented Windows.Media.Internal.AudioPolicyConfig factory (Windows 10 21H2+ / 11 layout).
/// Only affects apps that play on "Default" inside their own settings.
/// </summary>
static class AppRouting
{
    // IInspectable slots are declared by hand so this works without WinRT marshalling.
    [ComImport, Guid("ab3d4648-e242-459f-b02f-541c70306324"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioPolicyConfigFactory
    {
        void GetIids(); void GetRuntimeClassName(); void GetTrustLevel();
        void add_CtxVolumeChange(); void remove_CtxVolumeChanged();
        void add_RingerVibrateStateChanged(); void remove_RingerVibrateStateChange();
        void SetVolumeGroupGainForId(); void GetVolumeGroupGainForId();
        void GetActiveVolumeGroupForEndpointId(); void GetVolumeGroupsForEndpoint();
        void GetCurrentVolumeContext(); void SetVolumeGroupMuteForId(); void GetVolumeGroupMuteForId();
        void SetRingerVibrateState(); void GetRingerVibrateState();
        void SetPreferredChatApplication(); void ResetPreferredChatApplication(); void GetPreferredChatApplication();
        void GetCurrentChatApplications(); void add_ChatContextChanged(); void remove_ChatContextChanged();
        [PreserveSig] int SetPersistedDefaultAudioEndpoint(uint processId, int flow, int role, IntPtr deviceId);
        [PreserveSig] int GetPersistedDefaultAudioEndpoint(uint processId, int flow, int role, out IntPtr deviceId);
        [PreserveSig] int ClearAllPersistedApplicationDefaultEndpoints();
    }

    [DllImport("combase.dll", PreserveSig = false)]
    static extern void RoGetActivationFactory(IntPtr classId, [In] ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object factory);
    [DllImport("combase.dll", PreserveSig = false)]
    static extern void WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string s, int length, out IntPtr hstring);
    [DllImport("combase.dll")]
    static extern int WindowsDeleteString(IntPtr hstring);
    [DllImport("combase.dll")]
    static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out uint length);

    const string RenderInterface = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
    const string Prefix = @"\\?\SWD#MMDEVAPI#";
    const int FlowRender = 0;
    static readonly int[] Roles = { 0, 1, 2 }; // console, multimedia, communications

    static IAudioPolicyConfigFactory Factory()
    {
        const string cls = "Windows.Media.Internal.AudioPolicyConfig";
        WindowsCreateString(cls, cls.Length, out IntPtr h);
        try
        {
            var iid = typeof(IAudioPolicyConfigFactory).GUID;
            RoGetActivationFactory(h, ref iid, out object f);
            return (IAudioPolicyConfigFactory)f;
        }
        finally { WindowsDeleteString(h); }
    }

    /// <summary>The app's current per-app output (MMDevice ID), or "" when it follows the system default.</summary>
    public static string GetOutput(int pid) => GetPersisted(pid, FlowRender, 0);

    /// <summary>Per-app device for a flow (0 render, 1 capture) and role, or "" when it follows the system default.</summary>
    public static string GetPersisted(int pid, int flow, int role)
    {
        var f = Factory();
        Marshal.ThrowExceptionForHR(f.GetPersistedDefaultAudioEndpoint((uint)pid, flow, role, out IntPtr h));
        if (h == IntPtr.Zero) return "";
        try
        {
            string full = Marshal.PtrToStringUni(WindowsGetStringRawBuffer(h, out uint len), (int)len);
            if (full.StartsWith(Prefix)) full = full[Prefix.Length..];
            int i = full.IndexOf("#{", StringComparison.Ordinal);
            return i >= 0 ? full[..i] : full;
        }
        finally { WindowsDeleteString(h); }
    }

    /// <summary>Sends the app's audio to <paramref name="deviceId"/> (an MMDevice ID), or back to the system default when empty.</summary>
    public static void SetOutput(int pid, string deviceId)
    {
        var f = Factory();
        IntPtr h = IntPtr.Zero;
        try
        {
            if (!string.IsNullOrEmpty(deviceId))
            {
                string full = Prefix + deviceId + RenderInterface;
                WindowsCreateString(full, full.Length, out h);
            }
            foreach (int role in Roles)
                Marshal.ThrowExceptionForHR(f.SetPersistedDefaultAudioEndpoint((uint)pid, FlowRender, role, h));
        }
        finally { if (h != IntPtr.Zero) WindowsDeleteString(h); }
    }
}
