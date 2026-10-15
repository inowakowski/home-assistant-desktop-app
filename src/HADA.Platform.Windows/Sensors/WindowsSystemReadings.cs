using HADA.Core.Abstractions;

namespace HADA.Platform.Windows.Sensors;

/// <summary>What the service's sensors read, from Windows: its own calls, the console session and the registry.</summary>
public sealed class WindowsSystemReadings : ISystemReadings
{
    public CpuSample? ReadCpu()
    {
        if (!CpuTimes.TryRead(out var times))
        {
            return null;
        }

        // Kernel time already includes idle time, so the total is kernel + user.
        var total = times.Kernel + times.User;
        return new CpuSample(total - times.Idle, total);
    }

    public int? ReadMemoryLoadPercent() => SystemMemory.TryReadLoadPercent();

    public PowerStatus? ReadPower() => SystemPower.TryRead();

    public DateTimeOffset? ReadBootTime() => DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    public string? ReadConsoleUser() => ConsoleSession.TryReadUserName();

    public bool? ReadConsoleLocked() => ConsoleSession.TryRead()?.IsLocked;

    public string? ReadPendingRestart() => PendingRestart.TryRead();

    public IReadOnlyList<DiskUsage> ReadDisks() => Disks.Read();

    public WifiStatus ReadWifi() => WifiNetwork.TryRead();
}
