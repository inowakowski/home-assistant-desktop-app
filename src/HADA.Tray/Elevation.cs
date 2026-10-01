using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace HADA.Tray;

internal static class Elevation
{
    private const int ErrorCancelled = 1223;

    /// <summary>True only for an elevated administrator; with UAC, an admin's normal token does not count.</summary>
    public static bool IsElevated { get; } = GetIsElevated();

    /// <summary>Starts this app again as administrator. Returns <see langword="false"/> if the user declined the UAC prompt.</summary>
    public static bool TryRelaunchElevated(string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
            })?.Dispose();
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return false;
        }
    }

    private static bool GetIsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
