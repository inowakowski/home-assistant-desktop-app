using HADA.Core.Abstractions;
using HADA.Platform.Windows.Interop;
using Microsoft.Win32;

namespace HADA.Platform.Windows.Actions;

/// <summary>
/// Whether an app fills the screen, as Windows tells apps that ask whether to show a notification now: a full-screen
/// app, a game drawing to the screen itself, or presentation mode. Read in the user's session.
/// </summary>
public sealed class WindowsFullScreen : IFullScreen
{
    // QUERY_USER_NOTIFICATION_STATE.
    private const int Busy = 2;
    private const int RunningDirect3DFullScreen = 3;
    private const int PresentationMode = 4;
    private const int StoreApp = 7;

    public FullScreenKind? Read() =>
        NativeMethods.SHQueryUserNotificationState(out var state) != 0 ? null : state switch
        {
            Busy or StoreApp => FullScreenKind.App,
            RunningDirect3DFullScreen => FullScreenKind.Game,
            PresentationMode => FullScreenKind.Presentation,
            _ => FullScreenKind.None,
        };
}

/// <summary>
/// The dark look, as the "Choose your mode" setting of Windows has it: the apps' and Windows' own, both switched
/// together, then told to every window so that open apps change at once. Kept in the user's registry.
/// </summary>
public sealed class WindowsDarkMode : IDarkMode
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsLight = "AppsUseLightTheme";
    private const string SystemLight = "SystemUsesLightTheme";

    // WM_SETTINGCHANGE to every top-level window, waiting at most a second for any of them.
    private const uint SettingChange = 0x001A;
    private const uint AbortIfHung = 0x0002;
    private static readonly nint Broadcast = 0xFFFF;

    public bool CanSet => true;

    public bool? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(Key);

        // Without the value, Windows shows its light look.
        return key?.GetValue(AppsLight) is int light ? light == 0 : false;
    }

    public string? Set(bool dark)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(Key);
            key.SetValue(AppsLight, dark ? 0 : 1, RegistryValueKind.DWord);
            key.SetValue(SystemLight, dark ? 0 : 1, RegistryValueKind.DWord);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return ex.Message;
        }

        NativeMethods.SendMessageTimeout(Broadcast, SettingChange, 0, "ImmersiveColorSet", AbortIfHung, 1000, out _);
        return null;
    }
}

/// <summary>
/// Signs the user out as the Start menu does: apps are asked to close, and one with unsaved work can still hold it up.
/// Runs in the user's session, whose user it signs out.
/// </summary>
public sealed class WindowsSignOut : ISignOut
{
    private const uint LogOff = 0x0;

    // SHTDN_REASON_MAJOR_OTHER | SHTDN_REASON_FLAG_PLANNED: signing out is never an unplanned shutdown.
    private const uint Reason = 0x80000000;

    public bool CanSignOut => true;

    public string? SignOut() =>
        NativeMethods.ExitWindowsEx(LogOff, Reason) ? null : $"Windows did not sign out (error {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}).";
}
