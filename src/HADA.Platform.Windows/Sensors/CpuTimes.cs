using HADA.Platform.Windows.Interop;

namespace HADA.Platform.Windows.Sensors;

/// <summary>System-wide CPU times across all cores, in 100 ns ticks.</summary>
public readonly record struct CpuTimes(long Idle, long Kernel, long User)
{
    public static bool TryRead(out CpuTimes times)
    {
        var ok = NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user);
        times = new CpuTimes(idle, kernel, user);
        return ok;
    }

    /// <summary>Busy share of the elapsed CPU time between two samples, 0-100.</summary>
    public static double LoadPercent(CpuTimes previous, CpuTimes current)
    {
        // Kernel time already includes idle time, so the total is kernel + user.
        var idle = current.Idle - previous.Idle;
        var total = current.Kernel - previous.Kernel + (current.User - previous.User);
        return total <= 0 ? 0 : Math.Clamp(100.0 * (total - idle) / total, 0, 100);
    }
}
