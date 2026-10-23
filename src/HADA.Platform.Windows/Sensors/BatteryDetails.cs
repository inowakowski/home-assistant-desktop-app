using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using HADA.Core.Abstractions;

namespace HADA.Platform.Windows.Sensors;

/// <summary>
/// What the battery's driver tells besides its level, from the WMI classes of the ACPI battery: what it holds when
/// full against what it was made to hold, how many cycles it has been through, and the power going in or out. Asked
/// at most once a minute, as some firmware answers slowly; computers whose driver does not say leave them out.
/// </summary>
public static class BatteryDetails
{
    private const string Namespace = @"root\WMI";
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(1);
    private static readonly Lock Gate = new();
    private static (DateTime At, int? Health, int? Cycles, double? Watts) _last = (DateTime.MinValue, null, null, null);

    /// <summary><paramref name="power"/> with what the driver tells added.</summary>
    public static PowerStatus AddTo(PowerStatus power)
    {
        lock (Gate)
        {
            if (DateTime.UtcNow - _last.At > Fresh)
            {
                _last = (DateTime.UtcNow, ReadHealth(), ReadCycles(), ReadWatts());
            }

            return power with { HealthPercent = _last.Health, CycleCount = _last.Cycles, PowerWatts = _last.Watts };
        }
    }

    private static int? ReadHealth()
    {
        var full = Sum("SELECT FullChargedCapacity FROM BatteryFullChargedCapacity", "FullChargedCapacity");
        var design = Sum("SELECT DesignedCapacity FROM BatteryStaticData", "DesignedCapacity");
        return full is > 0 && design is > 0 ? (int)Math.Clamp(Math.Round(100 * full.Value / design.Value), 0, 100) : null;
    }

    private static int? ReadCycles() =>
        Sum("SELECT CycleCount FROM BatteryCycleCount", "CycleCount") is > 0 and var cycles ? (int)cycles : null;

    // The driver gives milliwatts: into the battery while it charges, out of it while it discharges, none while it rests.
    private static double? ReadWatts()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(Namespace, "SELECT Charging, Discharging, ChargeRate, DischargeRate FROM BatteryStatus");
            using var results = searcher.Get();
            double? milliwatts = null;
            foreach (var item in results)
            {
                using (item)
                {
                    var rate = item["Charging"] is true ? item["ChargeRate"] : item["Discharging"] is true ? item["DischargeRate"] : null;
                    milliwatts = (milliwatts ?? 0) + Math.Abs(Convert.ToDouble(rate ?? 0, CultureInfo.InvariantCulture));
                }
            }

            return milliwatts / 1000;
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException)
        {
            return null;
        }
    }

    /// <summary>The property summed over every battery; <see langword="null"/> where the class has none to give.</summary>
    private static double? Sum(string query, string property)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(Namespace, query);
            using var results = searcher.Get();
            double? sum = null;
            foreach (var item in results)
            {
                using (item)
                {
                    if (item[property] is { } value)
                    {
                        sum = (sum ?? 0) + Convert.ToDouble(value, CultureInfo.InvariantCulture);
                    }
                }
            }

            return sum;
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException)
        {
            return null;
        }
    }
}
