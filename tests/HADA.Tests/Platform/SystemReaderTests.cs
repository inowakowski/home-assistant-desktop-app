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
