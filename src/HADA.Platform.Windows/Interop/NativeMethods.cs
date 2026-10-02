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

    /// <summary>Length, in characters, of the list <see cref="CM_Get_Device_ID_List"/> returns. Returns a CONFIGRET code.</summary>
    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_List_SizeW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint CM_Get_Device_ID_List_Size(out uint length, string? filter, uint flags);

    /// <summary>Device instance ids, each terminated by a null character, with a second null after the last.</summary>
    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_ListW", StringMarshalling = StringMarshalling.Utf16)]
    public static unsafe partial uint CM_Get_Device_ID_List(string? filter, char* buffer, uint bufferLength, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint CM_Locate_DevNode(out uint deviceInstance, string deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW")]
    public static unsafe partial uint CM_Get_DevNode_Property(
        uint deviceInstance, in DevicePropertyKey key, out uint propertyType, byte* buffer, ref uint bufferSize, uint flags);

    /// <summary>How many paths and modes <see cref="QueryDisplayConfig"/> will return. Returns a Win32 error code.</summary>
    [LibraryImport("user32.dll")]
    public static partial int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    /// <summary>The modes are not used here, so they are received into a plain byte buffer of 64 bytes each.</summary>
    [LibraryImport("user32.dll")]
    public static unsafe partial int QueryDisplayConfig(
        uint flags, ref uint pathCount, DisplayConfigPath* paths, ref uint modeCount, byte* modes, nint currentTopology);
}

[StructLayout(LayoutKind.Sequential)]
internal struct DevicePropertyKey
{
    public Guid Category;
    public uint Id;
}

/// <summary>
/// DISPLAYCONFIG_PATH_INFO, a source (20 bytes), a target (48 bytes) and flags; only the target's identity,
/// connector type and availability are needed.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 72)]
internal struct DisplayConfigPath
{
    [FieldOffset(20)]
    public long TargetAdapterId;

    [FieldOffset(28)]
    public uint TargetId;

    /// <summary>DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.</summary>
    [FieldOffset(36)]
    public uint OutputTechnology;

    /// <summary>Non-zero while a monitor is connected to this output.</summary>
    [FieldOffset(60)]
    public int TargetAvailable;
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
