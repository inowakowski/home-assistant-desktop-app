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

    [PreserveSig]
    int OpenPropertyStore(uint access, out IPropertyStore? properties);
}

[ComImport]
[Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    void GetCount();

    void GetAt();

    /// <summary>The value must be released with <see cref="NativeMethods.PropVariantClear"/>.</summary>
    [PreserveSig]
    int GetValue(in DevicePropertyKey key, out PropVariant value);
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

    /// <param name="level">0 to 1.</param>
    /// <param name="eventContext">Identifies the caller to volume-change listeners; may be 0.</param>
    [PreserveSig]
    int SetMasterVolumeLevelScalar(float level, nint eventContext);

    void GetMasterVolumeLevel();

    [PreserveSig]
    int GetMasterVolumeLevelScalar(out float level);

    void SetChannelVolumeLevel();

    void SetChannelVolumeLevelScalar();

    void GetChannelVolumeLevel();

    void GetChannelVolumeLevelScalar();

    [PreserveSig]
    int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, nint eventContext);

    [PreserveSig]
    int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
}
