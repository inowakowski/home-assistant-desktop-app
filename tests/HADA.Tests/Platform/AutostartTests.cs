using System.Runtime.Versioning;
using HADA.Platform.Windows.Desktop;

namespace HADA.Tests.Platform;

public sealed class AutostartTests
{
    // Turned on in the new window, the entry must still start the tray app, which is what runs in the background.
    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void A_portable_copy_starts_its_tray_app_at_sign_in_whichever_window_turned_it_on()
    {
        var command = Autostart.PortableCommand(@"D:\Tools\HADA");

        Assert.Equal("\"D:\\Tools\\HADA\\tray\\HADA.Tray.exe\" --background", command);
    }
}
