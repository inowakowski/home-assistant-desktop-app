using HADA.Platform.Linux;

namespace HADA.Tests.Platform;

/// <summary>The lid, the screens, the backlight and the graphics card, as the kernel's folders on Linux say.</summary>
public sealed class LinuxHardwareTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hada-hw-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [InlineData("state:      open\n", true)]
    [InlineData("state:      closed\n", false)]
    public void The_lid_is_read_from_acpi(string state, bool open)
    {
        File("proc/acpi/button/lid/LID0/state", state);

        Assert.Equal(open, Hardware.ReadLidOpen());
    }

    [Fact]
    public void Without_a_lid_there_is_nothing_to_read() =>
        Assert.Null(Hardware.ReadLidOpen());

    [Fact]
    public void Screens_are_counted_and_the_laptops_own_is_not_external()
    {
        File("sys/class/drm/card1-eDP-1/status", "connected\n");
        File("sys/class/drm/card1-HDMI-A-1/status", "connected\n");
        File("sys/class/drm/card1-DP-2/status", "disconnected\n");
        File("sys/class/drm/card1/dev", "226:1\n");

        Assert.Equal(new DisplayCount(2, 1), Hardware.ReadDisplays());
    }

    [Fact]
    public void The_firmwares_backlight_is_preferred_to_a_raw_one()
    {
        Backlight("intel_backlight", "raw", level: 120000, max: 240000);
        Backlight("acpi_video0", "firmware", level: 7, max: 10);

        var backlight = Hardware.ReadBacklight();

        Assert.Equal(new Backlight("acpi_video0", 7, 10), backlight);
        Assert.Equal(70, backlight!.Value.Percent);
    }

    [Fact]
    public void A_desktop_has_no_backlight() =>
        Assert.Null(Hardware.ReadBacklight());

    [Theory]
    [InlineData(50, 120000)]
    [InlineData(100, 240000)]
    [InlineData(0, 1)]
    [InlineData(150, 240000)]
    public void A_percentage_becomes_a_level_that_keeps_the_screen_lit(double percent, int level) =>
        Assert.Equal(level, ScreenBrightnessControl.LevelFor(new Backlight("intel_backlight", 0, 240000), percent));

    [Fact]
    public void Amds_cards_say_how_busy_they_are()
    {
        File("sys/class/drm/card0/device/gpu_busy_percent", "37\n");
        File("sys/class/drm/card0-HDMI-A-1/status", "connected\n");

        Assert.Equal(37, Hardware.ReadGpuBusyPercent());
    }

    [Theory]
    [InlineData("12\n", 12)]
    [InlineData("12\n87\n", 87)]
    [InlineData("[N/A]\n", null)]
    public void Nvidia_smi_says_how_busy_the_busiest_card_is(string output, int? load) =>
        Assert.Equal(load, GpuLoadSensor.ParseNvidiaSmi(output));

    [Fact]
    public void Kde_says_quiet_through_dbus_and_gnome_through_its_settings()
    {
        Assert.True(DoNotDisturbSensor.ParseBusctlBool("b true\n"));
        Assert.False(DoNotDisturbSensor.ParseBusctlBool("b false\n"));
        Assert.Null(DoNotDisturbSensor.ParseBusctlBool("Unknown property or interface."));

        // GNOME is quiet when banners are not shown.
        var gnome = new DoNotDisturbSensor(null!, null!, (tool, _) => tool == "gsettings" ? new HADA.Platform.Linux.ToolResult(0, "false\n", string.Empty) : null);
        Assert.True(gnome.Read());
    }

    private LinuxHardware Hardware => new(_root);

    private void Backlight(string name, string type, int level, int max)
    {
        File($"sys/class/backlight/{name}/type", type + "\n");
        File($"sys/class/backlight/{name}/brightness", level + "\n");
        File($"sys/class/backlight/{name}/max_brightness", max + "\n");
    }

    private void File(string path, string text)
    {
        var full = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, text);
    }
}
