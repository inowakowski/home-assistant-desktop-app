using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace HADA.Platform.MacOS;

/// <summary>The calls into macOS itself: the kernel's statistics, Core Graphics, Core Audio and Core Foundation.</summary>
[SupportedOSPlatform("macos")]
internal static class MacNative
{
    private const string LibSystem = "/usr/lib/libSystem.dylib";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreAudio = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";

    // host_statistics: the CPU's ticks by state, and the virtual memory's pages by use.
    private const int HostCpuLoadInfo = 3;
    private const int HostCpuLoadInfoCount = 4;
    private const int HostVmInfo64 = 4;
    private const int HostVmInfo64Count = 38;

    // Indexes into vm_statistics64, counted in 32-bit words.
    private const int WireCount = 3;
    private const int PurgeableCount = 22;
    private const int CompressorPageCount = 32;
    private const int InternalPageCount = 35;

    private const int Success = 0;

    // A send right to the host, asked for once: every call to mach_host_self takes another.
    private static readonly Lazy<uint> Host = new(mach_host_self);

    /// <summary>The CPU's ticks spent busy, and all of them, across all cores.</summary>
    public static (long Busy, long Total)? ReadCpuTicks()
    {
        var info = new int[HostCpuLoadInfoCount];
        var count = (uint)HostCpuLoadInfoCount;
        if (host_statistics(Host.Value, HostCpuLoadInfo, info, ref count) != Success)
        {
            return null;
        }

        // User, system, idle and nice, each a 32-bit counter.
        var ticks = info.Select(value => (long)(uint)value).ToArray();
        return (ticks[0] + ticks[1] + ticks[3], ticks.Sum());
    }

    /// <summary>
    /// Memory in use, 0-100, the way Activity Monitor counts it: the apps' memory, the wired memory, and what the
    /// compressor holds. What the system only keeps as a cache is free.
    /// </summary>
    public static int? ReadMemoryLoadPercent()
    {
        var info = new int[HostVmInfo64Count];
        var count = (uint)HostVmInfo64Count;
        if (host_statistics64(Host.Value, HostVmInfo64, info, ref count) != Success
            || host_page_size(Host.Value, out var pageSize) != Success
            || ReadUInt64("hw.memsize") is not { } total || total == 0)
        {
            return null;
        }

        long Pages(int index) => (uint)info[index];
        var used = (Pages(InternalPageCount) - Pages(PurgeableCount) + Pages(WireCount) + Pages(CompressorPageCount)) * (long)pageSize;
        return (int)Math.Clamp(Math.Round(100d * used / total), 0, 100);
    }

    /// <summary>When the system was started, from <c>kern.boottime</c>.</summary>
    public static DateTimeOffset? ReadBootTime()
    {
        // A struct timeval: the seconds, then the microseconds.
        var buffer = new byte[16];
        var size = (nuint)buffer.Length;
        return sysctlbyname("kern.boottime", buffer, ref size, IntPtr.Zero, 0) == Success && size >= 8
            ? DateTimeOffset.FromUnixTimeSeconds(BitConverter.ToInt64(buffer, 0))
            : null;
    }

    public static ulong? ReadUInt64(string name)
    {
        var buffer = new byte[8];
        var size = (nuint)buffer.Length;
        return sysctlbyname(name, buffer, ref size, IntPtr.Zero, 0) == Success && size == 8 ? BitConverter.ToUInt64(buffer, 0) : null;
    }

    /// <summary>Time since the last keyboard, mouse or trackpad input in the user's session.</summary>
    public static TimeSpan? ReadIdleTime()
    {
        // kCGEventSourceStateHIDSystemState, kCGAnyInputEventType.
        var seconds = CGEventSourceSecondsSinceLastEventType(1, uint.MaxValue);
        return double.IsFinite(seconds) && seconds >= 0 ? TimeSpan.FromSeconds(seconds) : null;
    }

    /// <summary>The screens connected, and how many of them are not the Mac's own.</summary>
    public static (int Total, int External)? ReadDisplays()
    {
        var displays = new uint[32];
        if (CGGetOnlineDisplayList((uint)displays.Length, displays, out var count) != Success)
        {
            return null;
        }

        var online = displays.Take((int)count).ToList();
        return (online.Count, online.Count(display => CGDisplayIsBuiltin(display) == 0));
    }

    /// <summary>The name of the default output or input device, as the Sound settings show it; <see langword="null"/> without one.</summary>
    public static string? ReadAudioDeviceName(bool input)
    {
        // kAudioObjectSystemObject asked for kAudioHardwarePropertyDefaultOutputDevice or ...InputDevice.
        var address = new AudioObjectPropertyAddress(input ? FourCc("dIn ") : FourCc("dOut"), FourCc("glob"), 0);
        var size = (uint)sizeof(uint);
        if (AudioObjectGetPropertyData(1, ref address, 0, IntPtr.Zero, ref size, out uint device) != Success || device == 0)
        {
            return null;
        }

        // kAudioObjectPropertyName, a CFString the caller releases.
        var name = new AudioObjectPropertyAddress(FourCc("lnam"), FourCc("glob"), 0);
        size = (uint)IntPtr.Size;
        if (AudioObjectGetPropertyData(device, ref name, 0, IntPtr.Zero, ref size, out IntPtr text) != Success || text == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return ToString(text);
        }
        finally
        {
            CFRelease(text);
        }
    }

    /// <summary>A Core Foundation string's text.</summary>
    public static string ToString(IntPtr cfString)
    {
        var length = CFStringGetLength(cfString);
        var characters = new char[length];
        CFStringGetCharacters(cfString, new CFRange(0, length), characters);
        return new string(characters);
    }

    private static uint FourCc(string code) => (uint)(code[0] << 24 | code[1] << 16 | code[2] << 8 | code[3]);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct AudioObjectPropertyAddress(uint Selector, uint Scope, uint Element);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CFRange(nint Location, nint Length);

    [DllImport(LibSystem)]
    private static extern uint mach_host_self();

    [DllImport(LibSystem)]
    private static extern int host_statistics(uint host, int flavor, [Out] int[] info, ref uint count);

    [DllImport(LibSystem)]
    private static extern int host_statistics64(uint host, int flavor, [Out] int[] info, ref uint count);

    [DllImport(LibSystem)]
    private static extern int host_page_size(uint host, out nuint pageSize);

    [DllImport(LibSystem, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int sysctlbyname(string name, [Out] byte[] value, ref nuint size, IntPtr newValue, nuint newSize);

    [DllImport(CoreGraphics)]
    private static extern double CGEventSourceSecondsSinceLastEventType(int source, uint eventType);

    [DllImport(CoreGraphics)]
    private static extern int CGGetOnlineDisplayList(uint maxDisplays, [Out] uint[] displays, out uint count);

    [DllImport(CoreGraphics)]
    private static extern int CGDisplayIsBuiltin(uint display);

    [DllImport(CoreAudio)]
    private static extern int AudioObjectGetPropertyData(
        uint objectId, ref AudioObjectPropertyAddress address, uint qualifierSize, IntPtr qualifier, ref uint size, out uint data);

    [DllImport(CoreAudio)]
    private static extern int AudioObjectGetPropertyData(
        uint objectId, ref AudioObjectPropertyAddress address, uint qualifierSize, IntPtr qualifier, ref uint size, out IntPtr data);

    [DllImport(CoreFoundation)]
    private static extern nint CFStringGetLength(IntPtr text);

    // UTF-16, as Core Foundation keeps its strings.
    [DllImport(CoreFoundation, CharSet = CharSet.Unicode)]
    private static extern void CFStringGetCharacters(IntPtr text, CFRange range, [Out] char[] buffer);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr value);
}
