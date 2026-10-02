using HADA.Core;
using Microsoft.Win32;

namespace HADA.Tray;

/// <summary>
/// Whether the tray app starts when this user signs in.
/// </summary>
/// <remarks>
/// The installer registers the tray app to start for every user of the computer, which only an administrator can
/// change. So the choice is kept per user instead: the registered command line carries <see cref="Argument"/>, and a
/// tray started with it exits at once when its user turned autostart off.
/// <para>
/// A portable copy was not installed, so nothing starts it. Turning this on adds it to the user's own startup
/// programs, and turning it off removes it again; that entry is the one thing a portable copy writes outside its folder.
/// </para>
/// </remarks>
internal static class Autostart
{
    /// <summary>Marks a start made by Windows at sign-in, as opposed to one made by the user.</summary>
    public const string Argument = "--autostart";

    private const string KeyPath = @"Software\HADA";
    private const string ValueName = "StartWithWindows";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static string PortableRunValue => "HADA" + AppInstance.Suffix;

    /// <summary>Installed: on unless the user turned it off. Portable: off unless the user turned it on.</summary>
    public static bool IsEnabled
    {
        get
        {
            if (AppInstance.IsPortable)
            {
                using var run = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                return run?.GetValue(PortableRunValue) is string;
            }

            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(ValueName) is not int value || value != 0;
        }

        set
        {
            if (AppInstance.IsPortable)
            {
                using var run = Registry.CurrentUser.CreateSubKey(RunKeyPath);
                if (value)
                {
                    run.SetValue(PortableRunValue, $"\"{Environment.ProcessPath}\" --background", RegistryValueKind.String);
                }
                else
                {
                    run.DeleteValue(PortableRunValue, throwOnMissingValue: false);
                }

                return;
            }

            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            key.SetValue(ValueName, value ? 1 : 0, RegistryValueKind.DWord);
        }
    }
}
