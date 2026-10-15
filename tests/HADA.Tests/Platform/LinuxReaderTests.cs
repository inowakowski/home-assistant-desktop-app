using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Platform.Linux;

namespace HADA.Tests.Platform;

/// <summary>What the Linux readers make of what Linux says. Run on every system, with the text Linux would give.</summary>
public sealed class LinuxReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hada-linux-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Cpu_load_counts_waiting_for_the_disk_as_idle()
    {
        var first = Proc.ParseCpu("cpu  100 0 50 800 50 0 0 0 0 0\ncpu0 50 0 25 400 25 0 0 0 0 0\n");
        var second = Proc.ParseCpu("cpu  250 0 100 1550 100 0 0 0 0 0\ncpu0 1 1 1 1 1 0 0 0 0 0\n");

        Assert.Equal(new CpuSample(150, 1000), first);
        Assert.Equal(20, CpuSample.LoadPercent(first!.Value, second!.Value));
        Assert.Null(Proc.ParseCpu("intr 12345\n"));
        Assert.Null(Proc.ParseCpu(null));
    }

    [Fact]
    public void Memory_in_use_leaves_out_what_caches_would_give_back()
    {
        const string meminfo = "MemTotal:       16000000 kB\nMemFree:          500000 kB\nMemAvailable:    4000000 kB\nBuffers:          100 kB\n";

        Assert.Equal(75, Proc.ParseMemoryLoadPercent(meminfo));
        Assert.Null(Proc.ParseMemoryLoadPercent("MemTotal: 16000000 kB\n"));
    }

    [Fact]
    public void Boot_time_is_the_btime_line()
    {
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1760000000), Proc.ParseBootTime("cpu  1 2 3 4\nbtime 1760000000\nprocesses 99\n"));
        Assert.Null(Proc.ParseBootTime("cpu  1 2 3 4\n"));
    }

    [Fact]
    public void A_laptop_charging_reports_its_battery_and_the_charger()
    {
        Supply("BAT0", ("type", "Battery"), ("scope", "System"), ("capacity", "80"), ("status", "Charging"));
        Supply("AC", ("type", "Mains"), ("online", "1"));
        Supply("hidpp_battery_0", ("type", "Battery"), ("scope", "Device"), ("capacity", "5"), ("status", "Discharging"));

        Assert.Equal(new PowerStatus(HasBattery: true, BatteryPercent: 80, IsCharging: true, IsPluggedIn: true), PowerSupplies.Read(_root));
    }

    [Fact]
    public void Two_batteries_count_as_one_by_the_energy_they_hold()
    {
        Supply("BAT0", ("type", "Battery"), ("capacity", "100"), ("status", "Discharging"), ("energy_now", "20000000"), ("energy_full", "20000000"));
        Supply("BAT1", ("type", "Battery"), ("capacity", "0"), ("status", "Discharging"), ("energy_now", "0"), ("energy_full", "60000000"));
        Supply("ADP1", ("type", "Mains"), ("online", "0"));

        Assert.Equal(new PowerStatus(HasBattery: true, BatteryPercent: 25, IsCharging: false, IsPluggedIn: false), PowerSupplies.Read(_root));
    }

    [Fact]
    public void A_desktop_has_no_battery_and_is_plugged_in()
    {
        Supply("hidpp_battery_0", ("type", "Battery"), ("scope", "Device"), ("capacity", "60"));

        Assert.Equal(new PowerStatus(HasBattery: false, BatteryPercent: null, IsCharging: false, IsPluggedIn: true), PowerSupplies.Read(_root));
        Assert.Null(PowerSupplies.Read(Path.Combine(_root, "missing")));
    }

    [Fact]
    public void Disks_are_the_computers_own_once_each()
    {
        const long gigabyte = 1024L * 1024 * 1024;
        Mount[] mounts =
        [
            new("/home", "btrfs", DriveType.Fixed, 500 * gigabyte, 200 * gigabyte),
            new("/", "btrfs", DriveType.Fixed, 500 * gigabyte, 200 * gigabyte),
            new("/boot/efi", "vfat", DriveType.Fixed, gigabyte, gigabyte / 2),
            new("/snap/firefox/123", "squashfs", DriveType.Fixed, gigabyte, 0),
            new("/run/user/1000", "tmpfs", DriveType.Ram, gigabyte, gigabyte),
            new("/mnt/Data 2", "ext4", DriveType.Fixed, 1000 * gigabyte, 250 * gigabyte),
            new("/media/stick", "vfat", DriveType.Removable, 16 * gigabyte, 8 * gigabyte),
        ];

        var disks = MountedDisks.Choose(mounts);

        Assert.Equal(["root", "mnt_data_2"], disks.Select(disk => disk.Key));
        Assert.Equal(new DiskUsage("root", "/", 60, 200, 500), disks[0]);
        Assert.Equal(75, disks[1].UsedPercent);
        Assert.All(disks, disk => Assert.True(BuiltInEntityIds.IsDiskUsage(BuiltInEntityIds.DiskUsage(disk.Key))));
    }

    [Theory]
    [InlineData("/", "root")]
    [InlineData("/home", "home")]
    [InlineData("/mnt/Data 2", "mnt_data_2")]
    [InlineData("/2tb", "disk_2tb")]
    public void A_mount_point_makes_a_key_for_an_entity_id(string mountPoint, string key) =>
        Assert.Equal(key, MountedDisks.KeyFor(mountPoint));

    [Theory]
    [InlineData("disk_c_usage", true)]
    [InlineData("disk_root_usage", true)]
    [InlineData("disk_mnt_data_2_usage", true)]
    [InlineData("disk_usage", false)]
    [InlineData("disk_1_usage", false)]
    [InlineData("disk__usage", false)]
    [InlineData("disk_Root_usage", false)]
    public void Disk_ids_are_told_from_other_ids(string id, bool isDisk) =>
        Assert.Equal(isDisk, BuiltInEntityIds.IsDiskUsage(id));

    [Fact]
    public void Logind_tells_whose_session_is_on_the_screen_and_whether_it_is_locked()
    {
        Assert.Equal(new ConsoleSession("2", "ignacy", IsGreeter: false, IsLocked: false), LoginSessions.Parse("2", "Name=ignacy\nClass=user\nLockedHint=no\n"));
        Assert.Equal(new ConsoleSession("2", "ignacy", IsGreeter: false, IsLocked: true), LoginSessions.Parse("2", "Name=ignacy\nClass=user\nLockedHint=yes\n"));

        // The sign-in screen is nobody's, and counts as locked.
        Assert.Equal(new ConsoleSession("c1", string.Empty, IsGreeter: true, IsLocked: true), LoginSessions.Parse("c1", "Name=gdm\nClass=greeter\nLockedHint=no\n"));
    }

    [Fact]
    public void Without_logind_nothing_is_known_and_without_a_session_nobody_is_there()
    {
        var withoutLogind = new LoginSessions((_, _) => null);
        Assert.Null(withoutLogind.Read(out var known));
        Assert.False(known);

        var emptySeat = new LoginSessions((_, _) => new ToolResult(0, "\n", string.Empty));
        Assert.Null(emptySeat.Read(out known));
        Assert.True(known);

        var readings = new LinuxSystemReadings(emptySeat);
        Assert.Equal(string.Empty, readings.ReadConsoleUser());
        Assert.True(readings.ReadConsoleLocked());
        Assert.Null(new LinuxSystemReadings(withoutLogind).ReadConsoleLocked());
    }

    [Fact]
    public void A_session_on_the_screen_is_found_through_the_first_seat()
    {
        var calls = new List<string>();
        var sessions = new LoginSessions((tool, arguments) =>
        {
            calls.Add(string.Join(' ', arguments));
            return arguments[0] == "show-seat"
                ? new ToolResult(0, "7\n", string.Empty)
                : new ToolResult(0, "Name=ola\nClass=user\nLockedHint=no\n", string.Empty);
        });

        Assert.Equal("ola", new LinuxSystemReadings(sessions).ReadConsoleUser());
        Assert.False(new LinuxSystemReadings(sessions).ReadConsoleLocked());

        // Both sensors asked; logind was asked once.
        Assert.Equal(2, calls.Count);
        Assert.StartsWith("show-session 7", calls[1], StringComparison.Ordinal);
    }

    [Fact]
    public void A_restart_is_wanted_for_flagged_packages_and_for_a_replaced_kernel()
    {
        File("etc/debian_version", "13.1");
        File("proc/sys/kernel/osrelease", "6.12.1-amd64\n");
        Directory.CreateDirectory(Path.Combine(_root, "usr", "lib", "modules", "6.12.1-amd64"));
        Assert.Equal(string.Empty, PendingRestart.Read(_root));

        File("var/run/reboot-required", "*** System restart required ***");
        Assert.Equal("packages", PendingRestart.Read(_root));

        Directory.Delete(Path.Combine(_root, "usr", "lib", "modules", "6.12.1-amd64"));
        Directory.CreateDirectory(Path.Combine(_root, "usr", "lib", "modules", "6.12.4-amd64"));
        Assert.Equal("packages,kernel", PendingRestart.Read(_root));
    }

    [Fact]
    public void Where_neither_can_be_told_the_restart_sensor_is_left_out()
    {
        Directory.CreateDirectory(_root);

        Assert.Null(PendingRestart.Read(_root));
    }

    // On the CI's Ubuntu: what this computer says, read for real.
    [LinuxFact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void This_computer_is_read_with_plausible_numbers()
    {
        var readings = new LinuxSystemReadings(new LoginSessions());

        Assert.NotNull(readings.ReadCpu());
        Assert.InRange(Assert.NotNull(readings.ReadMemoryLoadPercent()), 1, 100);
        Assert.InRange(Assert.NotNull(readings.ReadBootTime()), DateTimeOffset.UtcNow.AddYears(-1), DateTimeOffset.UtcNow);
        Assert.NotNull(readings.ReadPower());
        Assert.Contains(readings.ReadDisks(), disk => disk.Key == "root");
    }

    private void Supply(string name, params (string File, string Value)[] values)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        foreach (var (file, value) in values)
        {
            System.IO.File.WriteAllText(Path.Combine(folder, file), value + "\n");
        }
    }

    private void File(string path, string text)
    {
        var full = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, text);
    }
}
