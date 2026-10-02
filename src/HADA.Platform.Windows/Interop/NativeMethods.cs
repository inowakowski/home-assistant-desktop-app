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

    /// <summary>Injects keyboard or mouse input into the calling session. Returns how many events were injected.</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    public static unsafe partial uint SendInput(uint count, Input* inputs, int size);

    /// <summary>The desktop's own window (Program Manager), or 0 when no shell is running.</summary>
    [LibraryImport("user32.dll")]
    public static partial nint GetShellWindow();

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessage(nint window, uint message, nint wParam, nint lParam);

    /// <summary>Puts the computer to sleep, or hibernates it. Needs the shutdown privilege; returns once the computer is awake again.</summary>
    [LibraryImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SetSuspendState(
        [MarshalAs(UnmanagedType.U1)] bool hibernate, [MarshalAs(UnmanagedType.U1)] bool force, [MarshalAs(UnmanagedType.U1)] bool wakeupEventsDisabled);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenProcessToken(nint process, uint desiredAccess, out nint token);

    [LibraryImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool LookupPrivilegeValue(string? systemName, string name, out long luid);

    /// <summary>Succeeds even when the privilege is not held; the last error is then <c>ERROR_NOT_ALL_ASSIGNED</c>.</summary>
    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AdjustTokenPrivileges(
        nint token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, in TokenPrivilege newState, uint bufferLength, nint previousState, nint returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("ole32.dll")]
    public static partial int PropVariantClear(ref PropVariant value);

    /// <summary>Returns a Win32 error code.</summary>
    [LibraryImport("wlanapi.dll")]
    public static partial uint WlanOpenHandle(uint clientVersion, nint reserved, out uint negotiatedVersion, out nint client);

    [LibraryImport("wlanapi.dll")]
    public static partial uint WlanCloseHandle(nint client, nint reserved);

    /// <summary>The returned WLAN_INTERFACE_INFO_LIST must be released with <see cref="WlanFreeMemory"/>.</summary>
    [LibraryImport("wlanapi.dll")]
    public static partial uint WlanEnumInterfaces(nint client, nint reserved, out nint interfaces);

    /// <summary>The returned data must be released with <see cref="WlanFreeMemory"/>.</summary>
    [LibraryImport("wlanapi.dll")]
    public static partial uint WlanQueryInterface(
        nint client, in Guid interfaceId, int opCode, nint reserved, out uint dataSize, out nint data, nint valueType);

    [LibraryImport("wlanapi.dll")]
    public static partial void WlanFreeMemory(nint memory);

    /// <summary>Reads a Windows Notification Facility state. Undocumented, but stable since Windows 8. Returns an NTSTATUS.</summary>
    [LibraryImport("ntdll.dll")]
    public static unsafe partial int NtQueryWnfStateData(
        in ulong stateName, nint typeId, nint explicitScope, out uint changeStamp, byte* buffer, ref uint bufferSize);
}

/// <summary>INPUT: a type followed by a union, of which the mouse member is the largest.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Input
{
    public const uint Mouse = 0;
    public const uint Keyboard = 1;

    public uint Type;
    public InputUnion Data;
}

[StructLayout(LayoutKind.Explicit)]
internal struct InputUnion
{
    [FieldOffset(0)]
    public MouseInput Mouse;

    [FieldOffset(0)]
    public KeyboardInput Keyboard;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MouseInput
{
    public int X;
    public int Y;
    public uint MouseData;
    public uint Flags;
    public uint Time;
    public nuint ExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct KeyboardInput
{
    public ushort VirtualKey;
    public ushort ScanCode;
    public uint Flags;
    public uint Time;
    public nuint ExtraInfo;
}

/// <summary>TOKEN_PRIVILEGES holding one privilege; the structure is packed to 4 bytes.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct TokenPrivilege
{
    public uint Count;
    public long Luid;
    public uint Attributes;
}

/// <summary>PROPVARIANT on 64-bit Windows; only string values are read from it.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PropVariant
{
    public const ushort TypeWideString = 31;

    public ushort Type;
    public ushort Reserved1;
    public ushort Reserved2;
    public ushort Reserved3;
    public nint Value;
    public nint Value2;
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
