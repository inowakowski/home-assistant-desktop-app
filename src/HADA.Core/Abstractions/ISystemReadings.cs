namespace HADA.Core.Abstractions;

/// <summary>
/// What the service's own sensors read from the operating system. The sensors are the same everywhere, so the
/// entities are too; each system has its own way of reading these.
/// </summary>
/// <remarks>Every method returns <see langword="null"/> where the value cannot be read, and the sensor waits for the next try.</remarks>
public interface ISystemReadings
{
    /// <summary>CPU time spent so far, across all cores; two samples give the load in between.</summary>
    CpuSample? ReadCpu();

    /// <summary>Physical memory in use, 0-100.</summary>
    int? ReadMemoryLoadPercent();

    /// <summary>The battery and the power supply.</summary>
    PowerStatus? ReadPower();

    /// <summary>When the system was started.</summary>
    DateTimeOffset? ReadBootTime();

    /// <summary>Who is signed in at the computer's own screen; empty while nobody is.</summary>
    string? ReadConsoleUser();

    /// <summary>Whether the session at the computer's own screen is locked; the sign-in screen counts as locked.</summary>
    bool? ReadConsoleLocked();

    /// <summary>
    /// What waits for a restart to be finished, such as installed updates, as short names joined by a comma; empty
    /// when nothing does. <see langword="null"/> where the system cannot tell, and the sensor is left out.
    /// </summary>
    string? ReadPendingRestart();

    /// <summary>The computer's own disks: not removable ones, network shares or memory.</summary>
    IReadOnlyList<DiskUsage> ReadDisks();

    /// <summary>The Wi-Fi network the computer is connected to; nothing where it has no Wi-Fi.</summary>
    WifiStatus ReadWifi();
}

/// <summary>CPU time in the system's own units: the part spent busy, and all of it.</summary>
public readonly record struct CpuSample(long Busy, long Total)
{
    /// <summary>Busy share of the CPU time that passed between two samples, 0-100.</summary>
    public static double LoadPercent(CpuSample previous, CpuSample current)
    {
        var total = current.Total - previous.Total;
        var busy = current.Busy - previous.Busy;
        return total <= 0 ? 0 : Math.Clamp(100.0 * busy / total, 0, 100);
    }
}

/// <param name="HasAdapter">False on a computer without Wi-Fi.</param>
/// <param name="Ssid">The network's name, or <see langword="null"/> when not connected, or when the system does not tell.</param>
/// <param name="SignalPercent">Signal quality, 0-100.</param>
/// <param name="IsConnectedUnnamed">Connected to a network whose name the system keeps to itself, as recent versions of macOS do.</param>
public readonly record struct WifiStatus(bool HasAdapter, string? Ssid, int? SignalPercent, bool IsConnectedUnnamed = false);

/// <param name="BatteryPercent"><see langword="null"/> when the system does not know the charge level.</param>
public readonly record struct PowerStatus(bool HasBattery, int? BatteryPercent, bool IsCharging, bool IsPluggedIn);

/// <param name="Key">What the entity id is made of: a drive letter on Windows, such as <c>c</c>; a name made of the mount point elsewhere, such as <c>root</c> or <c>home</c>.</param>
/// <param name="Label">What the entity is called after: <c>C</c>, <c>/</c>, <c>/home</c>.</param>
public readonly record struct DiskUsage(string Key, string Label, int UsedPercent, double FreeGigabytes, double TotalGigabytes);
