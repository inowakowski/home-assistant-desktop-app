using Avalonia;
using HADA.Core;
using HADA.Platform.Windows.Desktop;

namespace HADA.App;

internal static class Program
{
    /// <summary>Held for as long as the window is open; <see langword="null"/> for a snapshot, which may run beside it.</summary>
    internal static WindowRole? Role { get; private set; }

    /// <summary>Whether this is the administrator copy of the window, opened with "Unlock editing".</summary>
    internal static bool IsElevated => OperatingSystem.IsWindows() && Elevation.IsElevated;

    [STAThread]
    public static int Main(string[] args)
    {
        if (!args.Contains("--snapshot"))
        {
            // Started again, as from the tray icon while the window is open: that window comes to the front instead.
            Role = WindowRole.Take(IsElevated);
            if (Role is null)
            {
                return 0;
            }
        }

        using (Role)
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
    }

    /// <summary>Also what Avalonia's designer and tooling start the app with.</summary>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
