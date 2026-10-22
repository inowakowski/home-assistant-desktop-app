using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using HADA.Platform.Windows.Interop;

namespace HADA.Platform.Windows.Desktop;

public static class Elevation
{
    private const int ErrorCancelled = 1223;

    // WTS_CURRENT_SESSION, and the WTS_INFO_CLASS values WTSUserName and WTSDomainName.
    private const uint CurrentSession = 0xFFFFFFFF;
    private const int UserNameClass = 5;
    private const int DomainNameClass = 7;

    /// <summary>True only for an elevated administrator; with UAC, an admin's normal token does not count.</summary>
    public static bool IsElevated { get; } = GetIsElevated();

    /// <summary>
    /// Whether this process runs as the user signed in to its session. An administrator who confirms the UAC prompt
    /// is still that user. A standard user who types an administrator's name and password into it starts the program
    /// as that administrator, with that account's registry and folders. <see langword="false"/> also when it cannot
    /// be told, as in a session nobody is signed in to.
    /// </summary>
    public static bool RunsAsSessionUser()
    {
        var user = ReadSession(UserNameClass);
        if (string.IsNullOrEmpty(user))
        {
            return false;
        }

        // The domain of a local account is the computer's name.
        var domain = ReadSession(DomainNameClass);
        var account = string.IsNullOrEmpty(domain) ? user : $@"{domain}\{user}";
        using var identity = WindowsIdentity.GetCurrent();
        try
        {
            return new NTAccount(account).Translate(typeof(SecurityIdentifier)).Equals(identity.User);
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or Win32Exception or UnauthorizedAccessException)
        {
            // A domain account while no domain controller answers: the names are compared instead.
            return string.Equals(account, identity.Name, StringComparison.OrdinalIgnoreCase);
        }
    }

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

    private static string? ReadSession(int infoClass)
    {
        if (!NativeMethods.WTSQuerySessionInformation(0, CurrentSession, infoClass, out var buffer, out _) || buffer == 0)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            NativeMethods.WTSFreeMemory(buffer);
        }
    }
}
