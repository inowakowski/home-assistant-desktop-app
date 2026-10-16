using HADA.Core.Abstractions;
using HADA.Core.Unix;
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
        Assert.Equal(level, LinuxBacklight.LevelFor(new Backlight("intel_backlight", 0, 240000), percent));

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
        Assert.Equal(load, LinuxHardware.ParseNvidiaSmi(output));

    [Fact]
    public void Kde_says_quiet_through_dbus_and_gnome_through_its_settings()
    {
        Assert.True(DoNotDisturbSensor.ParseBusctlBool("b true\n"));
        Assert.False(DoNotDisturbSensor.ParseBusctlBool("b false\n"));
        Assert.Null(DoNotDisturbSensor.ParseBusctlBool("Unknown property or interface."));

        // GNOME is quiet when banners are not shown.
        var gnome = new DoNotDisturbSensor(null!, null!, (tool, _) => tool == "gsettings" ? new ToolResult(0, "false\n", string.Empty) : null);
        Assert.True(gnome.Read());
    }

    [Fact]
    public void Usb_devices_are_known_by_vendor_and_product_as_on_windows()
    {
        Usb("usb1", vendor: "1d6b", product: "0002", name: "xHCI Host Controller");
        Usb("1-1", vendor: "0bda", product: "8153", name: "USB 10/100/1000 LAN", serial: "000001");
        // An interface of the device, 1-1:1.0 in the kernel's words; Windows takes no colon in a folder's name.
        File($"sys/bus/usb/devices/1-1{(OperatingSystem.IsWindows() ? '_' : ':')}1.0/bInterfaceClass", "ff\n");
        Usb("1-2", vendor: "046d", product: "c52b", name: null, manufacturer: "Logitech");
        var devices = new LinuxUsbDevices(_root);

        Assert.True(devices.IsPresent("VID_0BDA&PID_8153"));
        Assert.True(devices.IsPresent("vid_046d"));
        Assert.True(devices.IsPresent(@"8153\000001"));
        Assert.False(devices.IsPresent("VID_1D6B"));
        Assert.False(devices.IsPresent(string.Empty));
        Assert.Equal(
            [
                new LinuxUsbDevice("Logitech", "VID_046D&PID_C52B", @"USB\VID_046D&PID_C52B"),
                new LinuxUsbDevice("USB 10/100/1000 LAN", "VID_0BDA&PID_8153", @"USB\VID_0BDA&PID_8153\000001"),
            ],
            devices.ConnectedModels());
    }

    private LinuxHardware Hardware => new(_root);

    private void Usb(string device, string vendor, string product, string? name, string? serial = null, string? manufacturer = null)
    {
        File($"sys/bus/usb/devices/{device}/idVendor", vendor + "\n");
        File($"sys/bus/usb/devices/{device}/idProduct", product + "\n");
        foreach (var (file, text) in new[] { ("product", name), ("serial", serial), ("manufacturer", manufacturer) })
        {
            if (text is not null)
            {
                File($"sys/bus/usb/devices/{device}/{file}", text + "\n");
            }
        }
    }

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
