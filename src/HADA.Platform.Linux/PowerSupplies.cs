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

        var batteries = new List<Battery>();
        bool? mains = null;
        foreach (var supply in Directory.EnumerateDirectories(folder))
        {
            var type = Value(supply, "type");
            if (type == "Battery" && Value(supply, "scope") != "Device" && Value(supply, "present") != "0")
            {
                batteries.Add(ReadBattery(supply));
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
        return new PowerStatus(HasBattery: true, Level(batteries), charging, pluggedIn)
        {
            TimeRemaining = TimeRemaining(batteries),
            HealthPercent = Health(batteries),
            CycleCount = batteries.Select(battery => battery.Cycles).Where(cycles => cycles > 0).Max(),
            PowerWatts = batteries.TrueForAll(battery => battery.Watts is not null) ? batteries.Sum(battery => battery.Watts!.Value) : null,
        };
    }

    /// <param name="Energy">What it holds now: µWh, or µAh where the kernel counts charge instead of energy.</param>
    /// <param name="Full">What it holds when full now, in the same unit.</param>
    /// <param name="Design">What it was made to hold, in the same unit.</param>
    /// <param name="Rate">How fast it fills or empties: µW, or µA with charge.</param>
    private sealed record Battery(int? Percent, double? Energy, double? Full, double? Design, double? Rate, double? Watts, int? Cycles, string Status);

    private static Battery ReadBattery(string supply)
    {
        // Energy in µWh with power in µW, or charge in µAh with current in µA; never a mix of the two.
        var byEnergy = Number(supply, "energy_now") is not null;
        var rate = byEnergy ? Number(supply, "power_now") : Number(supply, "current_now");
        var voltage = Number(supply, "voltage_now");
        double? watts = byEnergy ? rate / 1e6 : rate * voltage / 1e12;
        return new Battery(
            Number(supply, "capacity") is { } capacity ? (int)Math.Clamp(capacity, 0, 100) : null,
            byEnergy ? Number(supply, "energy_now") : Number(supply, "charge_now"),
            byEnergy ? Number(supply, "energy_full") : Number(supply, "charge_full"),
            byEnergy ? Number(supply, "energy_full_design") : Number(supply, "charge_full_design"),
            rate is { } value ? Math.Abs(value) : null,
            watts is { } power ? Math.Abs(power) : null,
            Number(supply, "cycle_count") is { } cycles ? (int)cycles : null,
            Value(supply, "status") ?? "Unknown");
    }

    /// <summary>Until empty while discharging, until full while charging; from what is left and how fast it goes.</summary>
    private static TimeSpan? TimeRemaining(List<Battery> batteries)
    {
        var discharging = batteries.Exists(battery => battery.Status == "Discharging");
        var charging = batteries.Exists(battery => battery.Status == "Charging");
        if (discharging == charging || !batteries.TrueForAll(battery => battery.Energy is not null && battery.Full is not null && battery.Rate is not null))
        {
            return null;
        }

        var rate = batteries.Sum(battery => battery.Rate!.Value);
        var left = discharging
            ? batteries.Sum(battery => battery.Energy!.Value)
            : batteries.Sum(battery => Math.Max(0, battery.Full!.Value - battery.Energy!.Value));
        return rate > 0 ? TimeSpan.FromHours(left / rate) : null;
    }

    private static int? Health(List<Battery> batteries)
    {
        if (!batteries.TrueForAll(battery => battery.Full is > 0 && battery.Design is > 0))
        {
            return null;
        }

        var full = batteries.Sum(battery => battery.Full!.Value);
        var design = batteries.Sum(battery => battery.Design!.Value);
        return (int)Math.Clamp(Math.Round(100 * full / design), 0, 100);
    }

    private static int? Level(List<Battery> batteries)
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
