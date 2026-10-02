using System.Runtime.InteropServices;

namespace HADA.Platform.Windows.Interop;

// Minimal Core Audio (MMDevice API) definitions. Vtable order matters: unused methods are declared only to keep
// the slots of the methods after them in the right place.

internal enum EDataFlow
{
    Render = 0,
    Capture = 1,
}

internal enum ERole
{
    Multimedia = 1,
    Communications = 2,
}

[ComImport]
[Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorComObject
{
}

[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    void EnumAudioEndpoints();

    [PreserveSig]
    int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice? endpoint);
}

[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig]
    int Activate(ref Guid iid, uint classContext, nint activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object? instance);
}

[ComImport]
[Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    void RegisterControlChangeNotify();

    void UnregisterControlChangeNotify();

    void GetChannelCount();

    void SetMasterVolumeLevel();

    void SetMasterVolumeLevelScalar();

    void GetMasterVolumeLevel();

    [PreserveSig]
    int GetMasterVolumeLevelScalar(out float level);

    void SetChannelVolumeLevel();

    void SetChannelVolumeLevelScalar();

    void GetChannelVolumeLevel();

    void GetChannelVolumeLevelScalar();

    void SetMute();

    [PreserveSig]
    int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
}
