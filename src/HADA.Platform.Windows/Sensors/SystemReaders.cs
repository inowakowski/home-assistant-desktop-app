using System.Runtime.InteropServices;
using System.Security;
using HADA.Platform.Windows.Interop;
using Microsoft.Win32;

namespace HADA.Platform.Windows.Sensors;

/// <param name="BatteryPercent"><see langword="null"/> when Windows does not know the charge level.</param>
public readonly record struct PowerStatus(bool HasBattery, int? BatteryPercent, bool IsCharging, bool IsPluggedIn);

public static class SystemPower
{
    private const byte Unknown = 255;
    private const byte ChargingFlag = 8;
    private const byte NoBatteryFlag = 128;

    public static PowerStatus? TryRead()
    {
        if (!NativeMethods.GetSystemPowerStatus(out var status))
        {
            return null;
        }

        var hasBattery = status.BatteryFlag != Unknown && (status.BatteryFlag & NoBatteryFlag) == 0;
        return new PowerStatus(
            hasBattery,
            hasBattery && status.BatteryLifePercent <= 100 ? status.BatteryLifePercent : null,
            hasBattery && (status.BatteryFlag & ChargingFlag) != 0,
            status.AcLineStatus == 1);
    }
}

public static class SystemMemory
{
    /// <summary>Physical memory in use, 0-100, or <see langword="null"/> when it cannot be read.</summary>
    public static int? TryReadLoadPercent()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return NativeMethods.GlobalMemoryStatusEx(ref status) ? (int)status.MemoryLoad : null;
    }
}

public readonly record struct ConsoleSessionInfo(uint SessionId, bool IsLocked);

public static class ConsoleSession
{
    private const int SessionInfoExClass = 25;
    private const int UserNameClass = 5;
    private const int SessionUnlocked = 1;

    // WTSINFOEXW is a DWORD level followed by an 8-byte aligned union, whose level 1 member starts with
    // SessionId, SessionState and SessionFlags (4 bytes each).
    private const int SessionIdOffset = 8;
    private const int SessionFlagsOffset = 16;

    /// <summary>
    /// Reads whether a session is locked; by default the one on the physical console. Works from a service.
    /// Returns <see langword="null"/> when there is no such session. A console showing the sign-in screen counts as locked.
    /// </summary>
    public static ConsoleSessionInfo? TryRead(uint? sessionId = null)
    {
        var id = sessionId ?? NativeMethods.WTSGetActiveConsoleSessionId();
        if (id == NativeMethods.NoActiveConsoleSession
            || !NativeMethods.WTSQuerySessionInformation(0, id, SessionInfoExClass, out var buffer, out var size)
            || buffer == 0)
        {
            return null;
        }

        try
        {
            if (size < SessionFlagsOffset + sizeof(int) || Marshal.ReadInt32(buffer) != 1)
            {
                return null;
            }

            return new ConsoleSessionInfo(
                (uint)Marshal.ReadInt32(buffer, SessionIdOffset),
                Marshal.ReadInt32(buffer, SessionFlagsOffset) != SessionUnlocked);
        }
        finally
        {
            NativeMethods.WTSFreeMemory(buffer);
        }
    }

    /// <summary>
    /// The account name of whoever is signed in to a session; by default the one on the physical console.
    /// Empty while the sign-in screen is shown, <see langword="null"/> when there is no such session.
    /// </summary>
    public static string? TryReadUserName(uint? sessionId = null)
    {
        var id = sessionId ?? NativeMethods.WTSGetActiveConsoleSessionId();
        if (id == NativeMethods.NoActiveConsoleSession
            || !NativeMethods.WTSQuerySessionInformation(0, id, UserNameClass, out var buffer, out _)
            || buffer == 0)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(buffer) ?? string.Empty;
        }
        finally
        {
            NativeMethods.WTSFreeMemory(buffer);
        }
    }
}

public static class UserInput
{
    /// <summary>Time since the last keyboard or mouse input in the calling session.</summary>
    public static TimeSpan? TryReadIdleTime()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return NativeMethods.GetLastInputInfo(ref info)
            ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time))
            : null;
    }
}

/// <summary>
/// Which apps are using the microphone or camera right now, from the usage history Windows keeps for its privacy
/// settings: an app whose last use has a start time but no stop time is still using the device.
/// </summary>
public static class MediaCapture
{
    public const string Microphone = "microphone";
    public const string Camera = "webcam";

    private const string ConsentStore = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\";
    private const string NonPackaged = "NonPackaged";

    public static IReadOnlyList<string> AppsUsing(string capability)
    {
        var bootTime = (DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64)).ToFileTimeUtc();
        var apps = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var store = hive.OpenSubKey(ConsentStore + capability);
                if (store is null)
                {
                    continue;
                }

                Collect(store, bootTime, apps);
                using var nonPackaged = store.OpenSubKey(NonPackaged);
                if (nonPackaged is not null)
                {
                    Collect(nonPackaged, bootTime, apps);
                }
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
            {
                // Without access to a hive, report what the other one shows.
            }
        }

        return [.. apps];
    }

    /// <summary>
    /// Desktop apps are keyed by their path with <c>#</c> for <c>\</c>, store apps by package family name:
    /// <c>C:#Program Files#App#app.exe</c> becomes <c>app.exe</c>, <c>Microsoft.WindowsCamera_8wekyb3d8bbwe</c> becomes <c>Microsoft.WindowsCamera</c>.
    /// </summary>
    public static string AppName(string keyName)
    {
        var lastSeparator = keyName.LastIndexOf('#');
        if (lastSeparator >= 0)
        {
            return keyName[(lastSeparator + 1)..];
        }

        var publisherSuffix = keyName.LastIndexOf('_');
        return publisherSuffix > 0 ? keyName[..publisherSuffix] : keyName;
    }

    /// <summary>
    /// Whether a usage record (FILETIME values) describes a use still going on. An app that crashes, or is using the
    /// device when Windows restarts, never gets a stop time; such a record is recognised by starting before the boot.
    /// </summary>
    public static bool IsInUse(long started, long stopped, long bootTime) =>
        started != 0 && stopped == 0 && started >= bootTime;

    private static void Collect(RegistryKey parent, long bootTime, SortedSet<string> apps)
    {
        foreach (var name in parent.GetSubKeyNames())
        {
            if (name == NonPackaged)
            {
                continue;
            }

            using var app = parent.OpenSubKey(name);
            if (app?.GetValue("LastUsedTimeStart") is long started
                && app.GetValue("LastUsedTimeStop") is long stopped
                && IsInUse(started, stopped, bootTime))
            {
                apps.Add(AppName(name));
            }
        }
    }
}
