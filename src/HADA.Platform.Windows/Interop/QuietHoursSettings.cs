using System.Runtime.InteropServices;

namespace HADA.Platform.Windows.Interop;

// The shell's quiet hours settings, which the Settings app's "Do not disturb" switch (Focus assist on Windows 10)
// goes through. Not documented; the two first methods are all HADA uses.
[ComImport]
[Guid("F53321FA-34F8-4B7F-B9A3-361877CB94CF")]
internal class QuietHoursSettingsComObject
{
}

[ComImport]
[Guid("6BFF4732-81EC-4FFB-AE67-B6C1BC29631F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IQuietHoursSettings
{
    /// <summary>The profile the user chose, e.g. <c>Microsoft.QuietHoursProfile.Unrestricted</c>.</summary>
    [PreserveSig]
    int GetUserSelectedProfile([MarshalAs(UnmanagedType.LPWStr)] out string profileId);

    [PreserveSig]
    int SetUserSelectedProfile([MarshalAs(UnmanagedType.LPWStr)] string profileId);
}
