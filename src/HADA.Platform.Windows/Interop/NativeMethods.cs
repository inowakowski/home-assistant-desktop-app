using System.Runtime.InteropServices;

namespace HADA.Platform.Windows.Interop;

internal static partial class NativeMethods
{
    public const uint NoActiveConsoleSession = 0xFFFFFFFF;

    /// <summary>FILETIME values (100 ns ticks); kernel time includes idle time.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetSystemPowerStatus(out SystemPowerStatus status);

    /// <summary><see cref="MemoryStatusEx.Length"/> must be set before the call.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool LockWorkStation();

    [LibraryImport("kernel32.dll")]
    public static partial uint WTSGetActiveConsoleSessionId();

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    /// <summary>Copies at most <paramref name="maxCount"/> - 1 characters plus a terminator; returns the copied length.</summary>
    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", SetLastError = true)]
    public static unsafe partial int GetWindowText(nint window, char* buffer, int maxCount);

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    /// <summary>Time of the last keyboard or mouse input in the calling session. <see cref="LastInputInfo.Size"/> must be set.</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetLastInputInfo(ref LastInputInfo info);

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WTSDisconnectSession(nint server, uint sessionId, [MarshalAs(UnmanagedType.Bool)] bool wait);

    /// <summary>The returned buffer must be released with <see cref="WTSFreeMemory"/>.</summary>
    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WTSQuerySessionInformation(nint server, uint sessionId, int infoClass, out nint buffer, out uint bytesReturned);

    [LibraryImport("wtsapi32.dll")]
    public static partial void WTSFreeMemory(nint memory);

    /// <summary>
    /// Subscribes a callback to a power setting; unlike <c>RegisterPowerSettingNotification</c> it needs neither a
    /// window nor a service control handler. Returns a Win32 error code.
    /// </summary>
    [LibraryImport("powrprof.dll")]
    public static unsafe partial uint PowerSettingRegisterNotification(
        in Guid setting, uint flags, DeviceNotifySubscribeParameters* recipient, out nint registration);

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerSettingUnregisterNotification(nint registration);
}

[StructLayout(LayoutKind.Sequential)]
internal struct SystemPowerStatus
{
    /// <summary>0 = on battery, 1 = plugged in, 255 = unknown.</summary>
    public byte AcLineStatus;

    /// <summary>Bit 8 = charging, bit 128 = no system battery; 255 = unknown.</summary>
    public byte BatteryFlag;

    /// <summary>0-100, or 255 when unknown.</summary>
    public byte BatteryLifePercent;

    public byte SystemStatusFlag;
    public uint BatteryLifeTime;
    public uint BatteryFullLifeTime;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MemoryStatusEx
{
    public uint Length;

    /// <summary>Physical memory in use, 0-100.</summary>
    public uint MemoryLoad;

    public ulong TotalPhysical;
    public ulong AvailablePhysical;
    public ulong TotalPageFile;
    public ulong AvailablePageFile;
    public ulong TotalVirtual;
    public ulong AvailableVirtual;
    public ulong AvailableExtendedVirtual;
}

[StructLayout(LayoutKind.Sequential)]
internal struct LastInputInfo
{
    public uint Size;

    /// <summary>Tick count (as <see cref="Environment.TickCount"/>) of the last input.</summary>
    public uint Time;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DeviceNotifySubscribeParameters
{
    /// <summary><c>ULONG Callback(PVOID context, ULONG type, PVOID setting)</c>.</summary>
    public delegate* unmanaged[Stdcall]<nint, uint, nint, uint> Callback;

    public nint Context;
}
