using System.Runtime.Versioning;
using HADA.Core.Abstractions;
using HADA.Core.Unix;
using HADA.Platform.MacOS;

namespace HADA.Tests.Platform;

/// <summary>macOS, read through what its own tools print, and on a Mac, read for real.</summary>
public sealed class MacTests
{

    [Theory]
    [InlineData("Now drawing from 'AC Power'\n -InternalBattery-0 (id=4653155)\t85%; charging; 1:10 remaining present: true\n", 85, true, true)]
    [InlineData("Now drawing from 'AC Power'\n -InternalBattery-0 (id=4653155)\t99%; finishing charge; 0:05 remaining present: true\n", 99, true, true)]
    [InlineData("Now drawing from 'Battery Power'\n -InternalBattery-0 (id=4653155)\t62%; discharging; 4:31 remaining present: true\n", 62, false, false)]
    [InlineData("Now drawing from 'AC Power'\n -InternalBattery-0 (id=4653155)\t80%; AC attached; not charging present: true\n", 80, false, true)]
    [InlineData("Now drawing from 'AC Power'\n -InternalBattery-0 (id=4653155)\t100%; charged; 0:00 remaining present: true\n", 100, false, true)]
    public void Pmset_says_where_the_power_comes_from_and_how_full_the_battery_is(string output, int percent, bool charging, bool pluggedIn) =>
        Assert.Equal(new PowerStatus(HasBattery: true, percent, charging, pluggedIn), MacParsers.ParsePmsetBattery(output));

    [Fact]
    public void A_mac_without_a_battery_only_draws_from_the_mains()
    {
        Assert.Equal(new PowerStatus(HasBattery: false, null, false, true), MacParsers.ParsePmsetBattery("Now drawing from 'AC Power'\n"));
        Assert.Null(MacParsers.ParsePmsetBattery("pmset: command not found"));
    }

    [Fact]
    public void The_lid_and_the_graphics_processor_are_found_in_the_io_registry()
    {
        Assert.True(MacParsers.ParseLidOpen("    | |   \"AppleClamshellState\" = No\n"));
        Assert.False(MacParsers.ParseLidOpen("    | |   \"AppleClamshellState\" = Yes\n"));
        Assert.Null(MacParsers.ParseLidOpen(string.Empty));

        const string Gpu = "    \"PerformanceStatistics\" = {\"Renderer Utilization %\"=8,\"Device Utilization %\"=12,\"Tiler Utilization %\"=5}\n";
        Assert.Equal(12, MacParsers.ParseGpuLoad(Gpu));
        Assert.Equal(40, MacParsers.ParseGpuLoad("\"PerformanceStatistics\" = {\"GPU Activity(%)\"=40}"));
        Assert.Null(MacParsers.ParseGpuLoad("\"PerformanceStatistics\" = {}"));

        var hardware = new MacHardware((tool, arguments) => new ToolResult(0, arguments.Contains("IOAccelerator") ? Gpu : "\"AppleClamshellState\" = No", string.Empty));
        Assert.True(hardware.ReadLidOpen());
        Assert.True(hardware.HasGpuLoad);
        Assert.Equal(12, hardware.ReadGpuLoadPercent());
    }

    [Fact]
    public void The_login_window_tells_who_is_at_the_screen_and_whether_it_is_locked()
    {
        var users = Users(
            Session("someone", onConsole: false, locked: false),
            Session("ignacy", onConsole: true, locked: true));

        Assert.Equal(new ConsoleSession("ignacy", IsLocked: true), MacParsers.ParseConsoleUsers(users));
        Assert.Equal(new ConsoleSession("ignacy", IsLocked: false), MacParsers.ParseConsoleUsers(Users(Session("ignacy", onConsole: true, locked: false))));
    }

    [Fact]
    public void Nobody_at_the_screen_is_the_login_window_which_counts_as_locked()
    {
        Assert.Equal(new ConsoleSession(string.Empty, IsLocked: true), MacParsers.ParseConsoleUsers(Users()));
        Assert.Equal(new ConsoleSession(string.Empty, IsLocked: true), MacParsers.ParseConsoleUsers(PropertyList("<dict/>")));
        Assert.Null(MacParsers.ParseConsoleUsers("not a property list"));
    }

    [Fact]
    public void Usb_devices_are_known_by_vendor_and_product_as_on_windows()
    {
        const string Tree = """
            <dict>
                <key>IORegistryEntryName</key><string>Root</string>
                <key>IORegistryEntryChildren</key>
                <array>
                    <dict>
                        <key>IORegistryEntryName</key><string>AppleT8103USBXHCI</string>
                        <key>IORegistryEntryChildren</key>
                        <array>
                            <dict>
                                <key>IORegistryEntryName</key><string>USB 10/100/1000 LAN</string>
                                <key>idVendor</key><integer>3034</integer>
                                <key>idProduct</key><integer>33107</integer>
                                <key>USB Product Name</key><string>USB 10/100/1000 LAN</string>
                                <key>USB Serial Number</key><string>000001</string>
                            </dict>
                            <dict>
                                <key>IORegistryEntryName</key><string>USB Receiver</string>
                                <key>idVendor</key><integer>1133</integer>
                                <key>idProduct</key><integer>50475</integer>
                                <key>USB Vendor Name</key><string>Logitech</string>
                            </dict>
                            <dict>
                                <key>IORegistryEntryName</key><string>Root Hub Simulation</string>
                                <key>idVendor</key><integer>1452</integer>
                                <key>idProduct</key><integer>32775</integer>
                            </dict>
                        </array>
                    </dict>
                </array>
            </dict>
            """;
        var devices = new MacUsbDevices((_, _) => new ToolResult(0, PropertyList(Tree), string.Empty));

        Assert.True(devices.IsPresent("VID_0BDA&PID_8153"));
        Assert.True(devices.IsPresent("vid_046d"));
        Assert.True(devices.IsPresent(@"8153\000001"));
        Assert.False(devices.IsPresent("VID_05AC"));
        Assert.Equal(
            [
                new MacUsbDevice("Logitech", "VID_046D&PID_C52B", @"USB\VID_046D&PID_C52B"),
                new MacUsbDevice("USB 10/100/1000 LAN", "VID_0BDA&PID_8153", @"USB\VID_0BDA&PID_8153\000001"),
            ],
            devices.ConnectedModels());
    }

    [Fact]
    public void The_wifi_network_is_named_where_macos_tells_and_connected_where_it_does_not()
    {
        const string Ports = "\nHardware Port: Ethernet\nDevice: en0\nEthernet Address: a4:83:e7:00:00:01\n\nHardware Port: Wi-Fi\nDevice: en1\nEthernet Address: a4:83:e7:00:00:02\n";
        Assert.Equal([new HardwarePort("Ethernet", "en0"), new HardwarePort("Wi-Fi", "en1")], MacParsers.ParseHardwarePorts(Ports));

        Assert.Equal((true, "Home Net"), MacParsers.ParseSsid("<dictionary> {\n  BSSID : <redacted>\n  InterfaceType : WiFi\n  SSID : Home Net\n}\n"));
        Assert.Equal((true, (string?)null), MacParsers.ParseSsid("<dictionary> {\n  SSID : <redacted>\n}\n"));
        Assert.Equal((false, (string?)null), MacParsers.ParseSsid("<dictionary> {\n  InterfaceType : WiFi\n  LinkStatusActive : FALSE\n}\n"));

        var hidden = new MacNetwork((tool, _) => new ToolResult(0, tool == "networksetup" ? Ports : "  SSID : <redacted>\n", string.Empty));
        Assert.Equal(new WifiStatus(HasAdapter: true, null, null, IsConnectedUnnamed: true), hidden.ReadWifi());

        var wired = new MacNetwork((tool, _) => new ToolResult(0, tool == "networksetup" ? "Hardware Port: Ethernet\nDevice: en0\n" : string.Empty, string.Empty));
        Assert.Equal(new WifiStatus(HasAdapter: false, null, null), wired.ReadWifi());
    }

    [Fact]
    public void Applescript_says_the_volume_of_the_output_and_the_input()
    {
        Assert.Equal(
            new VolumeSettings(44, 75, false),
            MacParsers.ParseVolumeSettings("output volume:44, input volume:75, alert volume:100, output muted:false\n"));
        Assert.Equal(
            new VolumeSettings(null, null, null),
            MacParsers.ParseVolumeSettings("output volume:missing value, input volume:missing value, alert volume:100, output muted:missing value\n"));
    }

    [Theory]
    [InlineData("A", (ushort)0x00)]
    [InlineData("Q", (ushort)0x0C)]
    [InlineData("Z", (ushort)0x06)]
    [InlineData("0", (ushort)0x1D)]
    [InlineData("5", (ushort)0x17)]
    [InlineData("F1", (ushort)0x7A)]
    [InlineData("F12", (ushort)0x6F)]
    [InlineData("Enter", (ushort)0x24)]
    [InlineData("Backspace", (ushort)0x33)]
    [InlineData("Delete", (ushort)0x75)]
    [InlineData("Left", (ushort)0x7B)]
    public void Keys_are_the_macs_own_of_the_same_name(string name, ushort keyCode)
    {
        Assert.True(HADA.Core.Input.KeyCombination.TryParse(name, out var combination));

        Assert.Equal(keyCode, MacKeys.KeyCodeOf(combination.VirtualKey));
        Assert.Null(MacKeys.MediaKeyOf(combination.VirtualKey));
    }

    [Fact]
    public void Media_keys_are_the_keyboards_own_and_modifiers_come_in_order()
    {
        Assert.True(HADA.Core.Input.KeyCombination.TryParse("MediaPlayPause", out var play));
        Assert.Equal(16, MacKeys.MediaKeyOf(play.VirtualKey));
        Assert.Null(MacKeys.KeyCodeOf(play.VirtualKey));
        Assert.True(HADA.Core.Input.KeyCombination.TryParse("MediaStop", out var stop));
        Assert.Null(MacKeys.MediaKeyOf(stop.VirtualKey));
        Assert.True(HADA.Core.Input.KeyCombination.TryParse("F24", out var f24));
        Assert.Null(MacKeys.KeyCodeOf(f24.VirtualKey));

        Assert.True(HADA.Core.Input.KeyCombination.TryParse("Win+Shift+Alt+Ctrl+L", out var all));
        Assert.Equal(
            [(0x3B, MacKeys.ControlFlag), (0x3A, MacKeys.OptionFlag), (0x38, MacKeys.ShiftFlag), (0x37, MacKeys.CommandFlag)],
            MacKeys.ModifiersOf(all.Modifiers).Select(modifier => ((int)modifier.KeyCode, modifier.Flag)));
    }

    [MacOSFact]
    [SupportedOSPlatform("macos")]
    public void Shortcuts_use_carbons_modifier_bits()
    {
        Assert.True(HADA.Core.Input.KeyCombination.TryParse("Ctrl+Alt+Shift+Win+L", out var combination));

        Assert.Equal(0x1000u | 0x800u | 0x200u | 0x100u, MacHotkeys.ModifiersOf(combination.Modifiers));
    }

    [Fact]
    public async Task A_mac_has_no_hibernate_button()
    {
        await using var bus = new HADA.Core.Messaging.ChannelEventBus();
        var registry = new HADA.Core.Entities.EntityRegistry(bus);
        using var actions = new HADA.Core.Actions.PowerActions(
            bus, registry, Microsoft.Extensions.Logging.Abstractions.NullLogger<HADA.Core.Actions.PowerActions>.Instance, new MacPowerControl());
        await actions.StartAsync(CancellationToken.None);

        Assert.Equal(["restart", "shutdown", "sleep"], registry.Entities.Select(entity => entity.Id).Order(StringComparer.Ordinal));
        await actions.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void An_app_starts_at_sign_in_the_way_the_finder_opens_it()
    {
        var agent = MacParsers.ReadPropertyList(MacAutostart.Agent("/Applications/HADA & Co.app/Contents/MacOS/HADA.App")) as Dictionary<string, object?>;

        Assert.NotNull(agent);
        Assert.Equal(MacAutostart.Label, agent["Label"]);
        Assert.Equal(["/usr/bin/open", "-g", "-a", "/Applications/HADA & Co.app", "--args", "--background"], agent["ProgramArguments"] as List<object?>);
        Assert.True(agent["RunAtLoad"] is true);
    }

    [Fact]
    public void A_program_outside_an_app_starts_itself()
    {
        var agent = MacParsers.ReadPropertyList(MacAutostart.Agent("/Users/me/hada/bin/HADA.App")) as Dictionary<string, object?>;

        Assert.Equal(["/Users/me/hada/bin/HADA.App", "--background"], agent!["ProgramArguments"] as List<object?>);
        Assert.Null(MacAutostart.BundleOf("/Applications/HADA.app/Contents/MacOS/service/HADA.Service"));
    }

    /// <summary>The service's and the session's readings on this Mac, through the kernel, Core Graphics, Core Audio and the tools.</summary>
    [MacOSFact]
    [SupportedOSPlatform("macos")]
    public async Task This_mac_is_read_with_plausible_numbers()
    {
        var system = new MacSystemReadings(new MacNetwork());
        var first = system.ReadCpu();
        await Task.Delay(200);
        var second = system.ReadCpu();
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.InRange(CpuSample.LoadPercent(first.Value, second.Value), 0, 100);

        Assert.InRange(system.ReadMemoryLoadPercent() ?? -1, 1, 100);
        Assert.InRange(system.ReadBootTime() ?? DateTimeOffset.MaxValue, DateTimeOffset.UtcNow.AddYears(-1), DateTimeOffset.UtcNow);
        Assert.NotNull(system.ReadPower());
        Assert.NotNull(system.ReadConsoleUser());
        Assert.Contains(system.ReadDisks(), disk => disk.Key == "root" && disk.TotalGigabytes > 1);
        _ = system.ReadWifi();

        Assert.NotNull(new MacSessionReadings().ReadIdleTime());

        var displays = new MacDisplayReadings();
        Assert.True(displays.HasActiveWindow);
        _ = displays.ReadActiveWindow();
        _ = displays.ReadDisplays();

        // A build agent may have no sound device; asking must still work.
        var audio = new MacAudioDevices();
        _ = audio.Read(AudioEndpoint.Speakers);
        _ = audio.ReadName(AudioEndpoint.Speakers);
        _ = audio.ReadName(AudioEndpoint.Microphone);

        // What records, the screens and the brightness, and whether keys may be pressed: asked, never refused.
        var session = new MacSessionReadings();
        _ = session.IsInUse(CaptureDevice.Microphone);
        _ = session.IsInUse(CaptureDevice.Camera);
        _ = new MacDisplayPower().ReadDisplayOn();
        _ = new MacScreenBrightness().ReadPercent();
        if (!MacKeyboard.IsAllowed())
        {
            Assert.Equal(MacKeyboard.NotAllowed, new MacKeyboard().Press(new HADA.Core.Input.KeyCombination(HADA.Core.Input.KeyModifiers.Shift, 0)));
        }

        // A media key built as the keyboard sends it; without Accessibility it goes nowhere.
        Assert.True(MacKeyboard.PressMediaKey(16));

        var hardware = new MacHardware();
        _ = hardware.ReadLidOpen();
        _ = hardware.HasGpuLoad;
        _ = new MacUsbDevices().ConnectedModels();
    }

    private static string Users(params string[] sessions) => PropertyList($"<dict><key>IOConsoleUsers</key><array>{string.Concat(sessions)}</array><key>IORegistryEntryName</key><string>Root</string></dict>");

    private static string Session(string user, bool onConsole, bool locked) =>
        $"<dict><key>kCGSSessionUserNameKey</key><string>{user}</string>"
        + $"<key>kCGSSessionOnConsoleKey</key>{(onConsole ? "<true/>" : "<false/>")}"
        + "<key>kCGSessionLoginDoneKey</key><true/>"
        + (locked ? "<key>CGSSessionScreenIsLocked</key><true/>" : string.Empty)
        + "</dict>";

    private static string PropertyList(string body) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        {body}
        </plist>
        """;
}
