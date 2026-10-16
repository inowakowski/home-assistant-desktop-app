using System.Collections.Concurrent;
using HADA.Core.Abstractions;
using HADA.Core.Actions;
using HADA.Core.Entities;
using HADA.Core.Messaging;
using HADA.Core.Models;
using HADA.Core.Sensors;
using HADA.Core.Unix;
using HADA.Platform.Linux;
using Microsoft.Extensions.Logging.Abstractions;

namespace HADA.Tests.Platform;

/// <summary>The screens off and on, their brightness, and what records, on the systems that share these.</summary>
public sealed class DisplayControlTests : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly ChannelEventBus _bus = new();
    private readonly EntityRegistry _registry;

    public DisplayControlTests() => _registry = new EntityRegistry(_bus);

    public ValueTask DisposeAsync() => _bus.DisposeAsync();

    [Fact]
    public async Task The_screens_are_turned_off_and_woken_and_said_to_be_on()
    {
        var display = new FakeDisplay();
        using var actions = new DisplayPowerActions(_bus, _registry, NullLogger<DisplayPowerActions>.Instance, display);
        using var sensor = new DisplayPowerSensor(_bus, _registry, display);
        await using var readings = _bus.Subscribe<TelemetryEvent>();
        await actions.StartAsync(CancellationToken.None);
        await sensor.StartAsync(CancellationToken.None);

        Assert.Equal(
            [BuiltInEntityIds.DisplayOn, BuiltInEntityIds.TurnOffDisplay, BuiltInEntityIds.WakeDisplay],
            _registry.Entities.Select(entity => entity.Id).Order(StringComparer.Ordinal));

        await _bus.PublishAsync(new ActionCommand { ActionId = BuiltInEntityIds.TurnOffDisplay });
        await _bus.PublishAsync(new ActionCommand { ActionId = BuiltInEntityIds.WakeDisplay });
        await WaitUntilAsync(() => display.Done.Count == 2);
        Assert.Equal(["off", "wake"], display.Done);

        TelemetryEvent? reading = null;
        await WaitUntilAsync(() => readings.TryRead(out reading) && reading.SensorId == BuiltInEntityIds.DisplayOn);
        Assert.Equal(BinaryState.On, reading!.State);

        await sensor.StopAsync(CancellationToken.None);
        await actions.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Where_the_screens_cannot_be_switched_or_told_nothing_is_registered()
    {
        var display = new FakeDisplay { CanSwitch = false, On = null };
        using var actions = new DisplayPowerActions(_bus, _registry, NullLogger<DisplayPowerActions>.Instance, display);
        using var sensor = new DisplayPowerSensor(_bus, _registry, display);
        await actions.StartAsync(CancellationToken.None);
        await sensor.StartAsync(CancellationToken.None);

        Assert.Empty(_registry.Entities);
        await actions.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task The_brightness_is_set_and_reported()
    {
        var brightness = new FakeBrightness();
        using var control = new ScreenBrightnessControl(_bus, _registry, NullLogger<ScreenBrightnessControl>.Instance, brightness);
        await control.StartAsync(CancellationToken.None);
        Assert.True(_registry.TryGet(BuiltInEntityIds.ScreenBrightness, out _));

        await _bus.PublishAsync(new ActionCommand { ActionId = BuiltInEntityIds.ScreenBrightness, Value = "62.6" });
        await WaitUntilAsync(() => brightness.Percent == 63);

        await control.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task What_records_is_told_where_the_system_does_not_name_the_apps()
    {
        await using var readings = _bus.Subscribe<TelemetryEvent>();
        using var sensor = new MediaCaptureSensor(_bus, _registry, new UnnamedCapture());
        await sensor.StartAsync(CancellationToken.None);

        var seen = new Dictionary<string, TelemetryEvent>();
        await WaitUntilAsync(() =>
        {
            while (readings.TryRead(out var reading))
            {
                seen[reading.SensorId] = reading;
            }

            return seen.Count == 2;
        });
        Assert.Equal(BinaryState.On, seen[BuiltInEntityIds.MicrophoneInUse].State);
        Assert.Equal(BinaryState.Off, seen[BuiltInEntityIds.CameraInUse].State);
        Assert.False(seen[BuiltInEntityIds.MicrophoneInUse].Attributes.ContainsKey("apps"));

        await sensor.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData("i 0\n", true)]
    [InlineData("i 3\n", false)]
    [InlineData("i 1\n", false)]
    [InlineData("i -1\n", null)]
    [InlineData("Unknown object '/org/gnome/Mutter/DisplayConfig'.", null)]
    public void Mutter_says_whether_the_screens_are_on(string output, bool? on) =>
        Assert.Equal(on, LinuxDisplayPower.ParseMutterPowerSaveMode(output));

    [Theory]
    [InlineData("DPMS (Energy Star):\n  Standby: 600    Suspend: 600    Off: 600\n  DPMS is Enabled\n  Monitor is On\n", true)]
    [InlineData("DPMS (Energy Star):\n  DPMS is Enabled\n  Monitor is Off\n", false)]
    [InlineData("DPMS (Energy Star):\n  DPMS is Enabled\n  Monitor is in Standby\n", false)]
    [InlineData("Keyboard Control:\n  auto repeat:  on\n", null)]
    public void Xset_says_whether_the_monitor_is_on(string output, bool? on) =>
        Assert.Equal(on, LinuxDisplayPower.ParseXsetMonitor(output));

    [Fact]
    public void Gnome_is_asked_through_mutter_and_told_its_power_save_mode()
    {
        var calls = new List<string>();
        var display = new LinuxDisplayPower(
            (tool, arguments) =>
            {
                calls.Add($"{tool} {string.Join(' ', arguments)}");
                return new ToolResult(0, "i 0\n", string.Empty);
            },
            desktop: "ubuntu:GNOME",
            isX11: false);

        Assert.True(display.CanSwitch);
        Assert.True(display.ReadDisplayOn());
        Assert.Null(display.TurnOff());
        Assert.EndsWith("set-property org.gnome.Mutter.DisplayConfig /org/gnome/Mutter/DisplayConfig org.gnome.Mutter.DisplayConfig PowerSaveMode i 3", calls[^1]);
    }

    [Fact]
    public void An_x11_desktop_without_mutter_goes_through_xset_and_wayland_without_either_cannot()
    {
        ToolResult? Run(string tool, string[] arguments) =>
            tool == "xset" ? new ToolResult(0, "  Monitor is Off\n", string.Empty) : new ToolResult(1, string.Empty, "no such object");

        var x11 = new LinuxDisplayPower(Run, desktop: "XFCE", isX11: true);
        Assert.True(x11.CanSwitch);
        Assert.False(x11.ReadDisplayOn());

        var wayland = new LinuxDisplayPower(Run, desktop: "sway", isX11: false);
        Assert.False(wayland.CanSwitch);
        Assert.Null(wayland.ReadDisplayOn());
        Assert.NotNull(wayland.Wake());
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        while (!condition())
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    private sealed class FakeDisplay : IDisplayPower
    {
        private readonly ConcurrentQueue<string> _done = new();

        public bool? On { get; set; } = true;

        public bool CanSwitch { get; set; } = true;

        public IReadOnlyList<string> Done => [.. _done];

        public bool? ReadDisplayOn() => On;

        public string? TurnOff()
        {
            _done.Enqueue("off");
            return null;
        }

        public string? Wake()
        {
            _done.Enqueue("wake");
            return null;
        }
    }

    private sealed class FakeBrightness : IScreenBrightness
    {
        private int _percent = 40;

        public int Percent => Volatile.Read(ref _percent);

        public int? ReadPercent() => Percent;

        public string? SetPercent(int percent)
        {
            Volatile.Write(ref _percent, percent);
            return null;
        }
    }

    private sealed class UnnamedCapture : ISessionReadings
    {
        public TimeSpan? ReadIdleTime() => null;

        public IReadOnlyList<string>? AppsUsing(CaptureDevice device) => null;

        public bool? IsInUse(CaptureDevice device) => device == CaptureDevice.Microphone;
    }
}
