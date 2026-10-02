using Microsoft.Win32;

namespace HADA.Tray;

/// <summary>
/// Whether the tray app starts when this user signs in.
/// </summary>
/// <remarks>
/// The installer registers the tray app to start for every user of the computer, which only an administrator can
/// change. So the choice is kept per user instead: the registered command line carries <see cref="Argument"/>, and a
/// tray started with it exits at once when its user turned autostart off.
/// </remarks>
internal static class Autostart
{
    /// <summary>Marks a start made by Windows at sign-in, as opposed to one made by the user.</summary>
    public const string Argument = "--autostart";

    private const string KeyPath = @"Software\HADA";
    private const string ValueName = "StartWithWindows";

    /// <summary>On unless the user turned it off.</summary>
    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(ValueName) is not int value || value != 0;
        }

        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            key.SetValue(ValueName, value ? 1 : 0, RegistryValueKind.DWord);
        }
    }
}
