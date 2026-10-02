using System.Diagnostics;
using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Messaging;
using HADA.Core.Models;
using HADA.Ipc;
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
        _host = new CustomSensorHost(_bus, _registry, _options, NullLogger<CustomSensorHost>.Instance);
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
