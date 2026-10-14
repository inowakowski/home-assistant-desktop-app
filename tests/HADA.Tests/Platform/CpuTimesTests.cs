using System.Runtime.Versioning;
using HADA.Platform.Windows.Sensors;

namespace HADA.Tests.Platform;

// Tests of sensors and actions of Windows.
[SupportedOSPlatform("windows")]
public class CpuTimesTests
{
    [WindowsFact]
    public void LoadPercent_is_busy_share_of_elapsed_time()
    {
        // 200 ticks elapsed (kernel 100 incl. 75 idle, user 100) => 125 busy => 62.5 %.
        var load = CpuTimes.LoadPercent(new CpuTimes(0, 0, 0), new CpuTimes(Idle: 75, Kernel: 100, User: 100));

        Assert.Equal(62.5, load);
    }

    [WindowsFact]
    public void LoadPercent_is_zero_when_no_time_elapsed()
    {
        var sample = new CpuTimes(10, 20, 30);

        Assert.Equal(0, CpuTimes.LoadPercent(sample, sample));
    }

    [WindowsFact]
    public void TryRead_returns_monotonic_system_times()
    {
        Assert.True(CpuTimes.TryRead(out var first));
        Assert.True(CpuTimes.TryRead(out var second));

        Assert.InRange(CpuTimes.LoadPercent(first, second), 0, 100);
        Assert.True(second.Kernel >= first.Kernel && first.Kernel >= first.Idle);
    }
}
