using System.Diagnostics;
using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Messaging;
using HADA.Core.Models;
using HADA.Ipc;
using HADA.Platform.Windows.Sensors;
using HADA.Service.CustomSensors;
using HADA.Service.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace HADA.Tests.Service;

public sealed class CustomSensorTests : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly ChannelEventBus _bus = new();
    private readonly EntityRegistry _registry;
    private readonly TestOptionsMonitor<CustomSensorOptions> _options = new(new CustomSensorOptions());
    private readonly CustomSensorHost _host;

    public CustomSensorTests()
    {
        _registry = new EntityRegistry(_bus);
        _host = new CustomSensorHost(
            _bus, _registry, _options, NullLogger<CustomSensorHost>.Instance, new HADA.Platform.Windows.WindowsDeviceDirectory());
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync(CancellationToken.None);
        _host.Dispose();
        await _bus.DisposeAsync();
    }

    [Fact]
    public async Task Text_sensor_is_registered_and_reports_its_value()
    {
        await using var readings = _bus.Subscribe<TelemetryEvent>();
        _options.Set(Options(new CustomSensorDefinition { Name = "Pokój", Type = CustomSensorType.Text, Value = "Biuro", Unit = " " }));

        await _host.StartAsync(CancellationToken.None);

        var reading = await ReadAsync(readings);
        Assert.Equal("pokoj", reading.SensorId);
        Assert.Equal("Biuro", reading.State);
        Assert.Equal(CustomSensorHost.Source, reading.Source);
        Assert.True(_registry.TryGet("pokoj", out var entity));
        Assert.Equal(EntityKind.Sensor, entity.Kind);
        Assert.Null(entity.UnitOfMeasurement);
    }

    [Fact]
    public async Task Process_sensor_is_a_binary_sensor_that_sees_running_programs()
    {
        using var current = Process.GetCurrentProcess();
        await using var readings = _bus.Subscribe<TelemetryEvent>();
        _options.Set(Options(
            new CustomSensorDefinition { Id = "tests_running", Name = "Tests", Type = CustomSensorType.ProcessRunning, Value = current.ProcessName + ".exe" },
            new CustomSensorDefinition { Id = "ghost_running", Name = "Ghost", Type = CustomSensorType.ProcessRunning, Value = "no-such-process-" + Guid.NewGuid().ToString("N") }));

        await _host.StartAsync(CancellationToken.None);

        var states = new Dictionary<string, string>();
        while (states.Count < 2)
        {
            var reading = await ReadAsync(readings);
            states[reading.SensorId] = reading.State;
        }

        Assert.Equal(BinaryState.On, states["tests_running"]);
        Assert.Equal(BinaryState.Off, states["ghost_running"]);
        Assert.True(_registry.TryGet("tests_running", out var entity));
        Assert.Equal(EntityKind.BinarySensor, entity.Kind);
    }

    [Fact]
    public async Task Device_sensor_is_a_binary_sensor_that_is_on_while_the_device_is_connected()
    {
        var connected = PnpDevices.PresentInstanceIds()[0];
        await using var readings = _bus.Subscribe<TelemetryEvent>();
        _options.Set(Options(
            new CustomSensorDefinition { Id = "docked", Name = "Docked", Type = CustomSensorType.DeviceConnected, Value = connected },
            new CustomSensorDefinition { Id = "ghost_dock", Name = "Ghost dock", Type = CustomSensorType.DeviceConnected, Value = "VID_FFFF&PID_FFFF" }));

        await _host.StartAsync(CancellationToken.None);

        var states = new Dictionary<string, string>();
        while (states.Count < 2)
        {
            var reading = await ReadAsync(readings);
            states[reading.SensorId] = reading.State;
        }

        Assert.Equal(BinaryState.On, states["docked"]);
        Assert.Equal(BinaryState.Off, states["ghost_dock"]);
        Assert.True(_registry.TryGet("docked", out var entity));
        Assert.Equal(EntityKind.BinarySensor, entity.Kind);
        Assert.Equal("connectivity", entity.DeviceClass);
    }

    [Fact]
    public void A_device_id_too_short_to_tell_devices_apart_is_rejected()
    {
        var sensor = new CustomSensorDefinition { Id = "docked", Name = "Docked", Type = CustomSensorType.DeviceConnected, Value = "USB" };

        Assert.NotNull(CustomSensorRules.Validate(sensor));
        Assert.Null(CustomSensorRules.Validate(sensor with { Value = "VID_0BDA&PID_8153" }));
    }

    [Fact]
    public async Task A_command_button_runs_its_command_when_pressed_and_only_then()
    {
        var marker = Path.Combine(Path.GetTempPath(), $"hada-button-{Guid.NewGuid():N}.txt");
        _options.Set(Options(new CustomSensorDefinition
        {
            Name = "Start backup",
            Type = CustomSensorType.CommandButton,
            Value = $"Set-Content -LiteralPath '{marker}' -Value 'pressed'",
        }));
        await using var readings = _bus.Subscribe<TelemetryEvent>();
        await _host.StartAsync(CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            while (!_registry.TryGet("start_backup", out _))
            {
                await Task.Delay(20, timeout.Token);
            }

            Assert.True(_registry.TryGet("start_backup", out var entity));
            Assert.Equal(EntityKind.Button, entity.Kind);
            Assert.False(File.Exists(marker));

            await _bus.PublishAsync(new ActionCommand { ActionId = "start_backup", Origin = "mqtt" });

            while (!File.Exists(marker))
            {
                await Task.Delay(50, timeout.Token);
            }

            // A button has no state to report.
            Assert.False(readings.TryRead(out _));
        }
        finally
        {
            File.Delete(marker);
        }
    }

    [Fact]
    public async Task A_launch_button_asks_the_tray_to_start_what_was_configured_not_what_was_sent()
    {
        _options.Set(Options(new CustomSensorDefinition
        {
            Id = "open_player",
            Name = "Open player",
            Type = CustomSensorType.LaunchButton,
            Value = """
                "C:\Program Files\Player\player.exe" --fullscreen
                """,
        }));
        await _host.StartAsync(CancellationToken.None);
        using var timeout = new CancellationTokenSource(Timeout);
        while (!_registry.TryGet("open_player", out _))
        {
            await Task.Delay(20, timeout.Token);
        }

        await using var commands = _bus.Subscribe<ActionCommand>();
        await _bus.PublishAsync(new ActionCommand { ActionId = "open_player", Value = "calc.exe", Origin = "mqtt" });

        ActionCommand launch;
        do
        {
            launch = await ReadAsync(commands);
        }
        while (launch.ActionId != SessionCommands.Launch);

        Assert.Equal("""
            "C:\Program Files\Player\player.exe" --fullscreen
            """, launch.Value);
        Assert.Equal("mqtt", launch.Origin);
    }

    [Fact]
    public async Task A_keys_button_asks_the_tray_to_press_the_configured_keys()
    {
        _options.Set(Options(new CustomSensorDefinition { Id = "mute_call", Name = "Mute call", Type = CustomSensorType.KeysButton, Value = "Ctrl+Shift+M" }));
        await _host.StartAsync(CancellationToken.None);
        using var timeout = new CancellationTokenSource(Timeout);
        while (!_registry.TryGet("mute_call", out _))
        {
            await Task.Delay(20, timeout.Token);
        }

        await using var commands = _bus.Subscribe<ActionCommand>();
        await _bus.PublishAsync(new ActionCommand { ActionId = "mute_call", Origin = "mqtt" });

        ActionCommand press;
        do
        {
            press = await ReadAsync(commands);
        }
        while (press.ActionId != SessionCommands.PressKeys);

        Assert.Equal("Ctrl+Shift+M", press.Value);
    }

    [Fact]
    public async Task Quick_actions_become_triggers_and_the_tray_is_told_about_them()
    {
        await using var commands = _bus.Subscribe<ActionCommand>();
        _options.Set(Options(
            new CustomSensorDefinition { Name = "Toggle lamp", Type = CustomSensorType.QuickAction, Value = "Ctrl+Alt+L" },
            new CustomSensorDefinition { Name = "Movie scene", Type = CustomSensorType.QuickAction },
            new CustomSensorDefinition { Name = "Room", Type = CustomSensorType.Text, Value = "Office" }));

        await _host.StartAsync(CancellationToken.None);

        var told = await ReadAsync(commands);
        Assert.Equal(SessionCommands.QuickActions, told.ActionId);
        Assert.Equal(
            [new QuickActionInfo("movie_scene", "Movie scene", string.Empty), new QuickActionInfo("toggle_lamp", "Toggle lamp", "Ctrl+Alt+L")],
            QuickActionInfo.Deserialize(told.Value));
        Assert.True(_registry.TryGet("toggle_lamp", out var entity));
        Assert.Equal(EntityKind.Trigger, entity.Kind);

        // Removing them all tells the tray so, with an empty list.
        _options.Set(Options());
        Assert.Empty(QuickActionInfo.Deserialize((await ReadAsync(commands)).Value));
        Assert.False(_registry.TryGet("toggle_lamp", out _));
    }

    [Theory]
    [InlineData(CustomSensorType.QuickAction, "", true)]
    [InlineData(CustomSensorType.QuickAction, "Ctrl+Alt+L", true)]
    [InlineData(CustomSensorType.QuickAction, "L", false)] // would take the plain key away from every program
    [InlineData(CustomSensorType.QuickAction, "Shift+L", false)]
    [InlineData(CustomSensorType.QuickAction, "Ctrl", false)]
    [InlineData(CustomSensorType.QuickAction, "Ctrl+Banana", false)]
    [InlineData(CustomSensorType.KeysButton, "Ctrl+Shift+M", true)]
    [InlineData(CustomSensorType.KeysButton, "F11", true)]
    [InlineData(CustomSensorType.KeysButton, "Win", true)]
    [InlineData(CustomSensorType.KeysButton, "", false)]
    [InlineData(CustomSensorType.KeysButton, "Ctrl+Banana", false)]
    public void Keys_and_shortcuts_are_checked_when_settings_are_saved(CustomSensorType type, string value, bool isValid)
    {
        var definition = new CustomSensorDefinition { Name = "Keys", Type = type, Value = value }.Normalize();

        Assert.Equal(isValid, CustomSensorRules.Validate(definition) is null);
    }

    [Fact]
    public async Task Changed_settings_add_replace_and_remove_sensors()
    {
        await using var changes = _bus.Subscribe<EntityRegistryChange>();
        var room = new CustomSensorDefinition { Id = "room", Name = "Room", Type = CustomSensorType.Text, Value = "Office" };
        _options.Set(Options(room));
        await _host.StartAsync(CancellationToken.None);
        Assert.IsType<EntityRegistered>(await ReadAsync(changes));

        // Same id, different kind: the old entity must be removed before the new one is announced.
        _options.Set(Options(room with { Type = CustomSensorType.ProcessRunning, Value = "explorer" }));
        Assert.Equal(EntityKind.Sensor, Assert.IsType<EntityUnregistered>(await ReadAsync(changes)).Entity.Kind);
        Assert.Equal(EntityKind.BinarySensor, Assert.IsType<EntityRegistered>(await ReadAsync(changes)).Entity.Kind);

        _options.Set(Options());
        Assert.IsType<EntityUnregistered>(await ReadAsync(changes));
        Assert.False(_registry.TryGet("room", out _));
    }

    [Fact]
    public async Task Invalid_and_built_in_ids_from_configuration_are_skipped()
    {
        _options.Set(Options(
            new CustomSensorDefinition { Id = "cpu_load", Name = "Hijack", Type = CustomSensorType.Text, Value = "1" },
            new CustomSensorDefinition { Name = "No value", Type = CustomSensorType.PowerShell },
            new CustomSensorDefinition { Name = "Fine", Type = CustomSensorType.Text, Value = "ok" }));

        await _host.StartAsync(CancellationToken.None);

        Assert.Equal("fine", Assert.Single(_registry.Entities).Id);
    }

    [Fact]
    public async Task PowerShell_output_is_returned_trimmed_with_non_ascii_text_intact()
    {
        var result = await PowerShellRunner.RunAsync("Write-Output ('zażółć ' + (20 + 22))", TimeSpan.FromSeconds(60), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("zażółć 42", result.Output);
    }

    [Fact]
    public async Task PowerShell_commands_that_hang_are_stopped()
    {
        await Assert.ThrowsAsync<TimeoutException>(
            () => PowerShellRunner.RunAsync("Start-Sleep -Seconds 120", TimeSpan.FromSeconds(3), CancellationToken.None));
    }

    [Fact]
    public void Saved_custom_sensors_bind_back_from_configuration()
    {
        var sensor = new CustomSensorDefinition
        {
            Id = "free_space",
            Name = "Free space",
            Type = CustomSensorType.PowerShell,
            Value = "[math]::Round((Get-PSDrive C).Free / 1GB)",
            Unit = "GB",
            IntervalSeconds = 300,
        };
        var data = StoredSettingsConfigurationProvider.ToConfiguration(new StoredSettings { CustomSensors = [sensor] });

        var bound = new ConfigurationBuilder().AddInMemoryCollection(data).Build()
            .GetSection(CustomSensorOptions.SectionName).Get<CustomSensorOptions>();

        Assert.Equal(sensor, Assert.Single(bound!.Items));
        Assert.Equal("measurement", CustomSensorRules.ToEntity(sensor).StateClass);
    }

    private static CustomSensorOptions Options(params CustomSensorDefinition[] sensors) => new() { Items = [.. sensors] };

    private static async Task<T> ReadAsync<T>(IEventSubscription<T> subscription)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        await using var enumerator = subscription.ReadAllAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        Assert.True(await enumerator.MoveNextAsync());
        return enumerator.Current;
    }
}
