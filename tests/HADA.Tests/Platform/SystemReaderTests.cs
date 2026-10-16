using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading.Channels;
using HADA.Core.Sensors;
using HADA.Platform.Windows.Interop;
using HADA.Platform.Windows.Sensors;

namespace HADA.Tests.Platform;

/// <summary>
/// Smoke tests against the real Windows APIs behind the built-in sensors. Values depend on the machine, so they check
/// the contract; a wrong struct layout or offset would fail or return out-of-range values.
/// </summary>
// Tests of sensors and actions of Windows.
[SupportedOSPlatform("windows")]
public class SystemReaderTests
{
    [WindowsFact]
    public void Memory_load_is_a_percentage()
    {
        Assert.InRange(Assert.NotNull(SystemMemory.TryReadLoadPercent()), 1, 100);
    }

    [WindowsFact]
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

    [WindowsFact]
    public void Session_information_is_read_at_the_right_offsets()
    {
        using var process = Process.GetCurrentProcess();
        var sessionId = (uint)process.SessionId;

        // Session 0 (a service or some CI agents) has no desktop to lock, but must still be readable.
        var session = Assert.NotNull(ConsoleSession.TryRead(sessionId));

        Assert.Equal(sessionId, session.SessionId);
    }

    [WindowsFact]
    public void Idle_time_is_never_negative()
    {
        if (UserInput.TryReadIdleTime() is { } idle)
        {
            Assert.True(idle >= TimeSpan.Zero);
        }
    }

    [WindowsTheory]
    [InlineData(@"C:#Program Files#Zoom#bin#Zoom.exe", "Zoom.exe")]
    [InlineData("Microsoft.WindowsCamera_8wekyb3d8bbwe", "Microsoft.WindowsCamera")]
    [InlineData("plain", "plain")]
    public void Capture_app_names_are_shortened(string registryKey, string expected)
    {
        Assert.Equal(expected, MediaCapture.AppName(registryKey));
    }

    [WindowsTheory]
    [InlineData(200, 0, 100, true)]
    [InlineData(200, 300, 100, false)]
    [InlineData(0, 0, 100, false)]
    [InlineData(50, 0, 100, false)] // Never stopped, but started before the last boot: a stale record.
    public void Capture_usage_counts_only_while_it_can_still_be_going_on(long started, long stopped, long bootTime, bool expected)
    {
        Assert.Equal(expected, MediaCapture.IsInUse(started, stopped, bootTime));
    }

    [WindowsFact]
    public void Capture_usage_can_be_read()
    {
        Assert.NotNull(MediaCapture.AppsUsing(MediaCapture.Microphone));
        Assert.NotNull(MediaCapture.AppsUsing(MediaCapture.Camera));
    }

    [WindowsFact]
    public void Connected_devices_are_listed_and_found_by_a_part_of_their_id_in_any_case()
    {
        var present = PnpDevices.PresentInstanceIds();

        Assert.NotEmpty(present);
        Assert.True(PnpDevices.IsPresent(present[0].ToLowerInvariant()));
        Assert.False(PnpDevices.IsPresent("VID_FFFF&PID_FFFF_NO_SUCH_DEVICE"));
        Assert.False(PnpDevices.IsPresent(string.Empty));
    }

    [WindowsFact]
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

    [WindowsFact]
    public void Connected_displays_are_counted_consistently()
    {
        // Not available in a session without a desktop, such as a service or some CI agents.
        if (Displays.TryRead() is { } displays)
        {
            Assert.InRange(displays.External, 0, displays.Total);
        }
    }

    [WindowsFact]
    public void The_console_user_can_be_read()
    {
        // Empty on the sign-in screen and on build agents without a console user; never null while there is a console.
        if (ConsoleSession.TryRead() is not null)
        {
            Assert.NotNull(ConsoleSession.TryReadUserName());
        }
    }

    [WindowsFact]
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

    [WindowsFact]
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

    [WindowsFact]
    public void Built_in_drives_are_listed_with_plausible_numbers()
    {
        var disks = Disks.Read();

        Assert.Contains(disks, disk => disk.Key == char.ToLowerInvariant(Environment.SystemDirectory[0]).ToString());
        Assert.All(disks, disk =>
        {
            Assert.InRange(disk.UsedPercent, 0, 100);
            Assert.InRange(disk.FreeGigabytes, 0, disk.TotalGigabytes);
            Assert.True(DiskUsageSensor.IsEntityId(DiskUsageSensor.EntityIdFor(disk.Key)));
        });
        Assert.False(DiskUsageSensor.IsEntityId("disk_usage"));
        Assert.False(DiskUsageSensor.IsEntityId("disk_1_usage"));
    }

    [WindowsFact]
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

    [WindowsFact]
    public void Do_not_disturb_is_one_of_the_known_modes_or_not_told_at_all()
    {
        if (QuietHours.TryRead() is { } mode)
        {
            Assert.True(Enum.IsDefined(mode));
        }
    }

    /// <summary>
    /// The profile the Settings app's switch chose is read, and chosen again unchanged: the way the switch is set,
    /// tried without turning "Do not disturb" on or off.
    /// </summary>
    [WindowsFact]
    public void The_do_not_disturb_profile_is_read_and_chosen_again_unchanged()
    {
        var profile = QuietHours.TryReadProfile();
        Assert.NotNull(profile);
        Assert.StartsWith("Microsoft.QuietHoursProfile.", profile, StringComparison.Ordinal);

        Assert.Null(QuietHours.TrySetProfile(profile));
        Assert.Equal(profile, QuietHours.TryReadProfile());
    }

    [WindowsFact]
    public void A_pending_restart_can_be_told_and_names_what_waits()
    {
        var reasons = PendingRestart.TryRead();

        Assert.NotNull(reasons);
        Assert.All(reasons.Split(',', StringSplitOptions.RemoveEmptyEntries), reason => Assert.Contains(reason, new[] { "windows_update", "servicing" }));
    }

    [WindowsFact]
    public void Screen_brightness_is_a_percentage_or_not_there_at_all()
    {
        // A desktop, and the machines that build this, have no screen Windows can dim.
        if (HADA.Platform.Windows.Actions.ScreenBrightness.TryRead() is { } percent)
        {
            Assert.InRange(percent, 0, 100);
        }
    }

    [WindowsFact]
    public void The_connected_networks_have_names_and_none_twice()
    {
        var networks = HADA.Platform.Windows.Network.WindowsNetworkLocation.Read();

        Assert.All(networks, name => Assert.False(string.IsNullOrWhiteSpace(name)));
        Assert.Equal(networks.Count, networks.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [WindowsFact]
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
