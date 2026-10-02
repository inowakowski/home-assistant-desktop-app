using System.Collections.Concurrent;
using System.Diagnostics;
using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Messaging;
using HADA.Core.Models;
using HADA.Ipc;
using HADA.Platform.Windows.Sensors;
using HADA.Service.Settings;
using Microsoft.Extensions.Options;

namespace HADA.Service.CustomSensors;

/// <summary>
/// Runs the sensors and buttons the user defined in settings. Follows settings changes: new ones start, changed
/// ones restart, and removed ones are unregistered, which also removes them from Home Assistant.
/// </summary>
public sealed partial class CustomSensorHost(
    IEventBus bus,
    IEntityRegistry registry,
    IOptionsMonitor<CustomSensorOptions> options,
    ILogger<CustomSensorHost> logger) : BackgroundService
{
    /// <summary><see cref="Core.Models.TelemetryEvent.Source"/> of custom sensor readings.</summary>
    public const string Source = "custom";

    // Configuration reloads fire several change notifications in a row; apply them once.
    private static readonly TimeSpan ReloadDelay = TimeSpan.FromMilliseconds(500);

    // A button may start something long, such as a backup; a sensor's command has to be quick.
    private static readonly TimeSpan ButtonCommandTimeout = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, RunningSensor> _running = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _changed = new(0);
    private readonly ConcurrentDictionary<string, byte> _busyButtons = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var registration = options.OnChange(_ => _changed.Release());

        // Subscribed before the first button is registered, so a press right after discovery is not missed.
        var commands = bus.Subscribe<ActionCommand>();
        var pressing = Task.Run(() => HandlePressesAsync(commands, stoppingToken), CancellationToken.None);
        try
        {
            while (true)
            {
                await ReconcileAsync(stoppingToken);

                await _changed.WaitAsync(stoppingToken);
                await Task.Delay(ReloadDelay, stoppingToken);
                while (_changed.Wait(0, stoppingToken))
                {
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            // The entities stay registered: the service is stopping, and they should not vanish from Home Assistant.
            foreach (var id in _running.Keys)
            {
                await StopSensorAsync(id);
            }

            await pressing;
        }
    }

    private async Task HandlePressesAsync(IEventSubscription<ActionCommand> commands, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var command in commands.ReadAllAsync(cancellationToken))
            {
                if (!_running.TryGetValue(command.ActionId, out var running) || !running.Definition.IsButton)
                {
                    continue;
                }

                var button = running.Definition;
                if (button.Type == CustomSensorType.LaunchButton)
                {
                    // The service has no desktop; the tray app in the user's session starts it.
                    await bus.PublishAsync(
                        new ActionCommand { ActionId = SessionCommands.Launch, Value = button.Value, Origin = command.Origin },
                        cancellationToken);
                    LogPressed(logger, button.Id, command.Origin);
                }
                else if (_busyButtons.TryAdd(button.Id, 0))
                {
                    LogPressed(logger, button.Id, command.Origin);
                    _ = Task.Run(() => RunButtonCommandAsync(button, cancellationToken), CancellationToken.None);
                }
                else
                {
                    LogStillRunning(logger, button.Id);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            await commands.DisposeAsync();
        }
    }

    private async Task RunButtonCommandAsync(CustomSensorDefinition button, CancellationToken cancellationToken)
    {
        try
        {
            var result = await PowerShellRunner.RunAsync(button.Value, ButtonCommandTimeout, cancellationToken);
            if (result.ExitCode != 0)
            {
                LogButtonFailed(
                    logger, button.Id, $"PowerShell exited with code {result.ExitCode}: {(result.Error.Length > 0 ? result.Error : "no error output")}");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            LogButtonFailed(logger, button.Id, ex.Message);
        }
        finally
        {
            _busyButtons.TryRemove(button.Id, out _);
        }
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        var wanted = new Dictionary<string, CustomSensorDefinition>(StringComparer.Ordinal);
        foreach (var configured in options.CurrentValue.Items.Take(CustomSensorRules.MaxSensors))
        {
            // Settings saved from the window are already validated; appsettings.json is not.
            var sensor = configured.Normalize();
            if (CustomSensorRules.Validate(sensor) is { } error)
            {
                LogSkipped(logger, error);
            }
            else if (!wanted.TryAdd(sensor.Id, sensor))
            {
                LogSkipped(logger, $"Two custom sensors share the ID '{sensor.Id}'.");
            }
        }

        foreach (var (id, running) in _running)
        {
            if (!wanted.TryGetValue(id, out var sensor) || sensor != running.Definition)
            {
                await StopSensorAsync(id);
                await registry.UnregisterAsync(id, cancellationToken);
            }
        }

        foreach (var (id, sensor) in wanted)
        {
            if (_running.ContainsKey(id))
            {
                continue;
            }

            await registry.RegisterAsync(CustomSensorRules.ToEntity(sensor), cancellationToken);
            var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // A button has nothing to read; it waits to be pressed.
            _running[id] = new RunningSensor(
                sensor,
                stopping,
                sensor.IsButton ? Task.CompletedTask : Task.Run(() => RunAsync(sensor, stopping.Token), CancellationToken.None));
            LogStarted(logger, sensor.Id, sensor.Type);
        }
    }

    private async Task StopSensorAsync(string id)
    {
        if (_running.TryRemove(id, out var running))
        {
            await running.Stopping.CancelAsync();
            await running.Loop;
            running.Stopping.Dispose();
        }
    }

    private async Task RunAsync(CustomSensorDefinition sensor, CancellationToken cancellationToken)
    {
        var publisher = new ChangeOnlyPublisher(bus, Source);
        try
        {
            if (sensor.Type == CustomSensorType.Text)
            {
                await publisher.PublishAsync(sensor.Id, sensor.Value, cancellationToken: cancellationToken);
                return;
            }

            // Logged when a sensor starts failing and when it recovers, not on every attempt.
            var failing = false;
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(sensor.IntervalSeconds));
            do
            {
                try
                {
                    await ReadAsync(sensor, publisher, cancellationToken);
                    if (failing)
                    {
                        LogRecovered(logger, sensor.Id);
                        failing = false;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (!failing)
                    {
                        LogReadFailed(logger, sensor.Id, ex.Message);
                        failing = true;
                    }
                }
            }
            while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static async Task ReadAsync(CustomSensorDefinition sensor, ChangeOnlyPublisher publisher, CancellationToken cancellationToken)
    {
        if (sensor.Type == CustomSensorType.ProcessRunning)
        {
            var count = CountProcesses(sensor.Value);
            await publisher.PublishAsync(
                sensor.Id,
                BinaryState.From(count > 0),
                new Dictionary<string, object?> { ["instances"] = count },
                cancellationToken);
            return;
        }

        if (sensor.Type == CustomSensorType.DeviceConnected)
        {
            await publisher.PublishAsync(
                sensor.Id, BinaryState.From(PnpDevices.IsPresent(sensor.Value)), cancellationToken: cancellationToken);
            return;
        }

        var result = await PowerShellRunner.RunAsync(sensor.Value, PowerShellRunner.DefaultTimeout, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"PowerShell exited with code {result.ExitCode}: {(result.Error.Length > 0 ? result.Error : "no error output")}");
        }

        if (result.Output.Length == 0)
        {
            throw new InvalidOperationException("the PowerShell command printed nothing");
        }

        await publisher.PublishAsync(sensor.Id, result.Output, cancellationToken: cancellationToken);
    }

    /// <summary>Counts processes by name, with or without <c>.exe</c>.</summary>
    public static int CountProcesses(string name)
    {
        var processes = Process.GetProcessesByName(
            name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^".exe".Length] : name);
        foreach (var process in processes)
        {
            process.Dispose();
        }

        return processes.Length;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Custom sensor '{SensorId}' ({Type}) started.")]
    private static partial void LogStarted(ILogger logger, string sensorId, CustomSensorType type);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipped a custom sensor: {Reason}")]
    private static partial void LogSkipped(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Custom sensor '{SensorId}' cannot be read: {Reason}. It keeps its last value until this works again.")]
    private static partial void LogReadFailed(ILogger logger, string sensorId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Custom button '{ButtonId}' pressed via {Origin}.")]
    private static partial void LogPressed(ILogger logger, string buttonId, string? origin);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Custom button '{ButtonId}' was pressed again while its command is still running; ignored.")]
    private static partial void LogStillRunning(ILogger logger, string buttonId);

    [LoggerMessage(Level = LogLevel.Error, Message = "The command of custom button '{ButtonId}' failed: {Reason}")]
    private static partial void LogButtonFailed(ILogger logger, string buttonId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Custom sensor '{SensorId}' can be read again.")]
    private static partial void LogRecovered(ILogger logger, string sensorId);

    private sealed record RunningSensor(CustomSensorDefinition Definition, CancellationTokenSource Stopping, Task Loop);
}
