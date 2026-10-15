using System.Globalization;
using HADA.Core.Abstractions;

namespace HADA.Platform.Linux;

/// <summary>
/// The batteries and power supplies the kernel lists in <c>/sys/class/power_supply</c>, a folder for each with a
/// file for each value.
/// </summary>
public static class PowerSupplies
{
    public const string DefaultFolder = "/sys/class/power_supply";

    /// <summary>
    /// The computer's own batteries and whether it is plugged in. The batteries of a mouse or a headset, which the
    /// kernel lists here too with the scope <c>Device</c>, are left out. With two batteries, as some laptops have,
    /// the level is that of both together.
    /// </summary>
    public static PowerStatus? Read(string folder = DefaultFolder)
    {
        if (!Directory.Exists(folder))
        {
            return null;
        }

        var batteries = new List<(int? Percent, double? Energy, double? Full, string Status)>();
        bool? mains = null;
        foreach (var supply in Directory.EnumerateDirectories(folder))
        {
            var type = Value(supply, "type");
            if (type == "Battery" && Value(supply, "scope") != "Device" && Value(supply, "present") != "0")
            {
                batteries.Add((
                    Number(supply, "capacity") is { } capacity ? (int)Math.Clamp(capacity, 0, 100) : null,
                    Number(supply, "energy_now") ?? Number(supply, "charge_now"),
                    Number(supply, "energy_full") ?? Number(supply, "charge_full"),
                    Value(supply, "status") ?? "Unknown"));
            }
            else if (type is "Mains" or "USB" or "USB_C" or "USB_PD" && Value(supply, "online") is { } online)
            {
                // Plugged in when any supply has power.
                mains = mains == true || online == "1";
            }
        }

        if (batteries.Count == 0)
        {
            return new PowerStatus(HasBattery: false, BatteryPercent: null, IsCharging: false, IsPluggedIn: mains ?? true);
        }

        var charging = batteries.Exists(battery => battery.Status == "Charging");

        // Without a power supply to ask, a battery that charges or is full tells it.
        var pluggedIn = mains ?? batteries.TrueForAll(battery => battery.Status is "Charging" or "Full" or "Not charging");
        return new PowerStatus(HasBattery: true, Level(batteries), charging, pluggedIn);
    }

    private static int? Level(List<(int? Percent, double? Energy, double? Full, string Status)> batteries)
    {
        if (batteries.Count > 1 && batteries.TrueForAll(battery => battery.Energy is not null && battery.Full is > 0))
        {
            var energy = batteries.Sum(battery => battery.Energy!.Value);
            var full = batteries.Sum(battery => battery.Full!.Value);
            return (int)Math.Clamp(Math.Round(100 * energy / full), 0, 100);
        }

        var known = batteries.Where(battery => battery.Percent is not null).Select(battery => battery.Percent!.Value).ToList();
        return known.Count > 0 ? (int)Math.Round(known.Average()) : null;
    }

    private static string? Value(string supply, string name) => Proc.ReadText(Path.Combine(supply, name))?.Trim();

    private static double? Number(string supply, string name) =>
        double.TryParse(Value(supply, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;
}
