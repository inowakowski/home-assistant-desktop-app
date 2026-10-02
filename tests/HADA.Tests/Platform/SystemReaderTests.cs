using System.Diagnostics;
using System.Threading.Channels;
using HADA.Platform.Windows.Interop;
using HADA.Platform.Windows.Sensors;

namespace HADA.Tests.Platform;

/// <summary>
/// Smoke tests against the real Windows APIs behind the built-in sensors. Values depend on the machine, so they check
/// the contract; a wrong struct layout or offset would fail or return out-of-range values.
/// </summary>
public class SystemReaderTests
{
    [Fact]
    public void Memory_load_is_a_percentage()
    {
        Assert.InRange(Assert.NotNull(SystemMemory.TryReadLoadPercent()), 1, 100);
    }

    [Fact]
    public void Power_status_is_consistent()
    {
        var power = Assert.NotNull(SystemPower.TryRead());

        if (power.BatteryPercent is { } percent)
        {
            Assert.True(power.HasBattery);
            Assert.InRange(percent, 0, 100);
        }

        if (!power.HasBattery)
        {
            Assert.False(power.IsCharging);
        }
    }

    [Fact]
    public void Session_information_is_read_at_the_right_offsets()
    {
        using var process = Process.GetCurrentProcess();
        var sessionId = (uint)process.SessionId;

        // Session 0 (a service or some CI agents) has no desktop to lock, but must still be readable.
        var session = Assert.NotNull(ConsoleSession.TryRead(sessionId));

        Assert.Equal(sessionId, session.SessionId);
    }

    [Fact]
    public void Idle_time_is_never_negative()
    {
        if (UserInput.TryReadIdleTime() is { } idle)
        {
            Assert.True(idle >= TimeSpan.Zero);
        }
    }

    [Theory]
    [InlineData(@"C:#Program Files#Zoom#bin#Zoom.exe", "Zoom.exe")]
    [InlineData("Microsoft.WindowsCamera_8wekyb3d8bbwe", "Microsoft.WindowsCamera")]
    [InlineData("plain", "plain")]
    public void Capture_app_names_are_shortened(string registryKey, string expected)
    {
        Assert.Equal(expected, MediaCapture.AppName(registryKey));
    }

    [Theory]
    [InlineData(200, 0, 100, true)]
    [InlineData(200, 300, 100, false)]
    [InlineData(0, 0, 100, false)]
    [InlineData(50, 0, 100, false)] // Never stopped, but started before the last boot: a stale record.
    public void Capture_usage_counts_only_while_it_can_still_be_going_on(long started, long stopped, long bootTime, bool expected)
    {
        Assert.Equal(expected, MediaCapture.IsInUse(started, stopped, bootTime));
    }

    [Fact]
    public void Capture_usage_can_be_read()
    {
        Assert.NotNull(MediaCapture.AppsUsing(MediaCapture.Microphone));
        Assert.NotNull(MediaCapture.AppsUsing(MediaCapture.Camera));
    }

    [Fact]
    public void Connected_devices_are_listed_and_found_by_a_part_of_their_id_in_any_case()
    {
        var present = PnpDevices.PresentInstanceIds();

        Assert.NotEmpty(present);
        Assert.True(PnpDevices.IsPresent(present[0].ToLowerInvariant()));
        Assert.False(PnpDevices.IsPresent("VID_FFFF&PID_FFFF_NO_SUCH_DEVICE"));
        Assert.False(PnpDevices.IsPresent(string.Empty));
    }

    [Fact]
    public void Usb_devices_are_offered_once_per_model_with_a_name_and_a_vendor_and_product_id()
    {
        var devices = PnpDevices.ConnectedUsbDevices();

        Assert.All(devices, device =>
        {
            Assert.Matches("^VID_[0-9A-F]{4}&PID_[0-9A-F]{4}$", device.MatchId);
            Assert.False(string.IsNullOrWhiteSpace(device.Name));
            Assert.True(PnpDevices.IsPresent(device.MatchId));
        });
        Assert.Equal(devices.Count, devices.Select(device => device.MatchId).Distinct().Count());
    }

    [Fact]
    public void Connected_displays_are_counted_consistently()
    {
        // Not available in a session without a desktop, such as a service or some CI agents.
        if (Displays.TryRead() is { } displays)
        {
            Assert.InRange(displays.External, 0, displays.Total);
        }
    }

    [Fact]
    public void The_console_user_can_be_read()
    {
        // Empty on the sign-in screen and on build agents without a console user; never null while there is a console.
        if (ConsoleSession.TryRead() is not null)
        {
            Assert.NotNull(ConsoleSession.TryReadUserName());
        }
    }

    [Fact]
    public void The_address_windows_routes_through_is_a_real_one()
    {
        if (NetworkAddress.TryRead() is { } network)
        {
            Assert.True(System.Net.IPAddress.TryParse(network.Address, out var address));
            Assert.False(System.Net.IPAddress.IsLoopback(address));
            Assert.Contains(network.ConnectionType, new[] { "ethernet", "wifi", "other" });
            Assert.Matches("^([0-9A-F]{2}(:[0-9A-F]{2})*)?$", network.MacAddress);
        }
    }

    [Fact]
    public void Wifi_is_read_without_misreading_the_structures()
    {
        var wifi = WifiNetwork.TryRead();

        if (wifi.Ssid is { } ssid)
        {
            Assert.True(wifi.HasAdapter);
            Assert.InRange(ssid.Length, 0, 32);
            Assert.DoesNotContain('\0', ssid);
            Assert.InRange(Assert.NotNull(wifi.SignalPercent), 0, 100);
        }
    }

    [Fact]
    public void Built_in_drives_are_listed_with_plausible_numbers()
    {
        var disks = Disks.Read();

        Assert.Contains(disks, disk => disk.Letter == char.ToLowerInvariant(Environment.SystemDirectory[0]));
        Assert.All(disks, disk =>
        {
            Assert.InRange(disk.UsedPercent, 0, 100);
            Assert.InRange(disk.FreeGigabytes, 0, disk.TotalGigabytes);
            Assert.True(DiskUsageSensor.IsEntityId(DiskUsageSensor.EntityIdFor(disk.Letter)));
        });
        Assert.False(DiskUsageSensor.IsEntityId("disk_usage"));
        Assert.False(DiskUsageSensor.IsEntityId("disk_1_usage"));
    }

    [Fact]
    public void Gpu_load_is_a_percentage_where_windows_reports_it()
    {
        // Not reported on a computer without a graphics driver that has these counters, such as some build agents.
        using var gpu = GpuLoad.TryOpen();
        if (gpu is null)
        {
            return;
        }

        Thread.Sleep(300);
        if (gpu.TryRead() is { } load)
        {
            Assert.InRange(load, 0, 100);
        }
    }

    [Fact]
    public void Do_not_disturb_is_one_of_the_known_modes_or_not_told_at_all()
    {
        if (QuietHours.TryRead() is { } mode)
        {
            Assert.True(Enum.IsDefined(mode));
        }
    }

    [Fact]
    public async Task Display_state_is_reported_as_soon_as_it_is_watched()
    {
        var changes = Channel.CreateUnbounded<(Guid Setting, int Value)>();
        using var listener = new PowerSettingListener((setting, value) => changes.Writer.TryWrite((setting, value)));

        Assert.True(listener.TryRegister(PowerSettingListener.ConsoleDisplayState, out var error), $"error {error}");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (setting, value) = await changes.Reader.ReadAsync(timeout.Token);
        Assert.Equal(PowerSettingListener.ConsoleDisplayState, setting);
        Assert.InRange(value, 0, 2);
    }
}
