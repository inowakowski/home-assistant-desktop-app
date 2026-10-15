using System.Security;
using Microsoft.Win32;

namespace HADA.Platform.Windows.Sensors;

/// <summary>Whether Windows is waiting for a restart to finish installing something.</summary>
public static class PendingRestart
{
    // The two places Windows itself looks at before it says "Restart required" in its settings.
    private const string WindowsUpdateKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired";
    private const string ServicingKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending";

    /// <summary>
    /// What is waiting: <c>windows_update</c>, <c>servicing</c> (a Windows feature or component), both joined by a
    /// comma, or empty when nothing is. <see langword="null"/> when it cannot be told.
    /// </summary>
    public static string? TryRead()
    {
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            var reasons = new List<string>(2);
            if (Exists(machine, WindowsUpdateKey))
            {
                reasons.Add("windows_update");
            }

            if (Exists(machine, ServicingKey))
            {
                reasons.Add("servicing");
            }

            return string.Join(',', reasons);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static bool Exists(RegistryKey machine, string path)
    {
        using var key = machine.OpenSubKey(path);
        return key is not null;
    }
}
