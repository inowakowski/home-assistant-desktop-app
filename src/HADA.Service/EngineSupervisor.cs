using System.Text.Json;
using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Engine.Mqtt;
using HADA.Engine.WebSocket;
using HADA.Ipc;
using HADA.Service.Settings;
using Microsoft.Extensions.Options;

namespace HADA.Service;

/// <summary>
/// Owns the communication engines: one per MQTT server, and one for the Home Assistant WebSocket API. Whenever an
/// engine's settings or the set of disabled entities change, e.g. after settings are saved from the window, that
/// engine is stopped and recreated with the new values; a server that was removed is disconnected from.
/// </summary>
public sealed partial class EngineSupervisor : IHostedService, IAsyncDisposable
{
    // Configuration reloads fire several change notifications in a row; apply them once.
    private static readonly TimeSpan ReloadDelay = TimeSpan.FromMilliseconds(500);

    private readonly IEventBus _bus;
    private readonly IEntityRegistry _registry;
    private readonly TelemetryCache? _telemetry;
    private readonly IOptionsMonitor<MqttOptions> _mqttOptions;
    private readonly IOptionsMonitor<MqttServersOptions> _mqttServers;
    private readonly IOptionsMonitor<HaWebSocketOptions> _homeAssistantOptions;
    private readonly IOptionsMonitor<EntityOptions> _entityOptions;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly EngineSlot<HaWebSocketOptions> _homeAssistant;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _reloadLock = new();
    private readonly List<IDisposable> _changeRegistrations = [];

    // Replaced as a whole, so the status can be read while settings are applied.
    private volatile EngineSlot<MqttOptions>[] _mqtt = [];
    private CancellationTokenSource? _pendingReload;
    private bool _stopped;

    public EngineSupervisor(
        IEventBus bus,
        IEntityRegistry registry,
        IOptionsMonitor<MqttOptions> mqttOptions,
        IOptionsMonitor<MqttServersOptions> mqttServers,
        IOptionsMonitor<HaWebSocketOptions> homeAssistantOptions,
        IOptionsMonitor<EntityOptions> entityOptions,
        ILoggerFactory loggerFactory,
        TelemetryCache? telemetry = null)
    {
        _bus = bus;
        _registry = registry;
        _telemetry = telemetry;
        _mqttOptions = mqttOptions;
        _mqttServers = mqttServers;
        _homeAssistantOptions = homeAssistantOptions;
        _entityOptions = entityOptions;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<EngineSupervisor>();
        _homeAssistant = new EngineSlot<HaWebSocketOptions>(
            options => !string.IsNullOrWhiteSpace(options.BaseUrl) && !string.IsNullOrWhiteSpace(options.AccessToken),
            (options, filter) => new HaWebSocketEngine(
                bus, registry, Options.Create(options), loggerFactory.CreateLogger<HaWebSocketEngine>(), filter));
    }

    /// <summary>One entry per MQTT server, or a single unconfigured one when there is none; then the WebSocket engine.</summary>
    public IReadOnlyList<EngineStatus> GetStatus()
    {
        var mqtt = _mqtt;
        var statuses = new List<EngineStatus>(mqtt.Length + 1);
        foreach (var slot in mqtt)
        {
            if (slot is { Engine: { } engine, Options: { } options })
            {
                statuses.Add(new EngineStatus(MqttEngine.EngineName, slot.IsConfigured, engine.State, options.Id, options.Name ?? string.Empty));
            }
        }

        if (statuses.Count == 0)
        {
            statuses.Add(new EngineStatus(MqttEngine.EngineName, false, EngineConnectionState.Disconnected));
        }

        if (_homeAssistant.Engine is { } homeAssistant)
        {
            statuses.Add(new EngineStatus(homeAssistant.Name, _homeAssistant.IsConfigured, homeAssistant.State));
        }

        return statuses;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await ApplyAsync(cancellationToken).ConfigureAwait(false);

        AddRegistration(_mqttOptions.OnChange(_ => ScheduleReload()));
        AddRegistration(_mqttServers.OnChange(_ => ScheduleReload()));
        AddRegistration(_homeAssistantOptions.OnChange(_ => ScheduleReload()));
        AddRegistration(_entityOptions.OnChange(_ => ScheduleReload()));
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _stopped = true;
            foreach (var registration in _changeRegistrations)
            {
                registration.Dispose();
            }

            // Side by side: each engine says goodbye to its own Home Assistant, and none should wait for another.
            await Task.WhenAll(AllSlots().Select(slot => slot.StopAsync(cancellationToken))).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var slot in AllSlots())
        {
            await slot.DisposeAsync().ConfigureAwait(false);
        }
    }

    private IEnumerable<EngineSlot> AllSlots() => [.. _mqtt, _homeAssistant];

    private void AddRegistration(IDisposable? registration)
    {
        if (registration is not null)
        {
            _changeRegistrations.Add(registration);
        }
    }

    private void ScheduleReload()
    {
        CancellationTokenSource debounce;
        lock (_reloadLock)
        {
            _pendingReload?.Cancel();
            _pendingReload = debounce = new CancellationTokenSource();
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(ReloadDelay, debounce.Token).ConfigureAwait(false);
                await ApplyAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer change.
            }
            catch (Exception ex)
            {
                LogReloadFailed(_logger, ex);
            }
        });
    }

    private async Task ApplyAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stopped)
            {
                return;
            }

            var filter = _entityOptions.CurrentValue.ToFilter();
            var anyRecreated = await ApplyMqttAsync(filter, cancellationToken).ConfigureAwait(false);
            anyRecreated |= await _homeAssistant.ApplyAsync(_homeAssistantOptions.CurrentValue, filter, _logger, cancellationToken)
                .ConfigureAwait(false);

            if (anyRecreated && _telemetry is not null)
            {
                // A new engine has seen no readings yet, and sensors that report only changes may stay silent for
                // hours. Engines cache what they receive while still connecting, so this is not lost.
                foreach (var reading in _telemetry.Latest)
                {
                    if (_registry.TryGet(reading.SensorId, out _))
                    {
                        await _bus.PublishAsync(reading, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Brings the MQTT engines in line with the servers in the settings, matched by their ids.</summary>
    private async Task<bool> ApplyMqttAsync(EntityFilter filter, CancellationToken cancellationToken)
    {
        var servers = MqttServersOptions.Resolve(_mqttServers.CurrentValue, _mqttOptions.CurrentValue);
        var current = _mqtt;
        var anyRecreated = false;

        foreach (var removed in current.Where(slot => servers.All(server => server.Id != slot.Id)))
        {
            LogServerRemoved(_logger, removed.Options?.Name is { Length: > 0 } name ? name : removed.Options?.Host ?? removed.Id);
            await removed.StopAsync(cancellationToken).ConfigureAwait(false);
            await removed.DisposeAsync().ConfigureAwait(false);
        }

        var next = new List<EngineSlot<MqttOptions>>(servers.Count);
        foreach (var server in servers)
        {
            var slot = Array.Find(current, slot => slot.Id == server.Id) ?? new EngineSlot<MqttOptions>(
                options => !string.IsNullOrWhiteSpace(options.Host),
                (options, engineFilter) => new MqttEngine(
                    _bus, _registry, Options.Create(options), _loggerFactory.CreateLogger<MqttEngine>(), engineFilter),
                server.Id);
            anyRecreated |= await slot.ApplyAsync(server, filter, _logger, cancellationToken).ConfigureAwait(false);
            next.Add(slot);
        }

        _mqtt = [.. next];
        return anyRecreated;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Settings changed; restarting the {EngineName} engine.")]
    private static partial void LogRestarting(ILogger logger, string engineName);

    [LoggerMessage(Level = LogLevel.Information, Message = "The MQTT server '{Server}' was removed from the settings; disconnected from it.")]
    private static partial void LogServerRemoved(ILogger logger, string server);

    [LoggerMessage(Level = LogLevel.Error, Message = "Applying changed settings failed.")]
    private static partial void LogReloadFailed(ILogger logger, Exception exception);

    private abstract class EngineSlot
    {
        private volatile ICommunicationEngine? _engine;

        public ICommunicationEngine? Engine
        {
            get => _engine;
            protected set => _engine = value;
        }

        public bool IsConfigured { get; protected set; }

        public Task StopAsync(CancellationToken cancellationToken) =>
            Engine?.StopAsync(cancellationToken) ?? Task.CompletedTask;

        public async ValueTask DisposeAsync()
        {
            if (Engine is { } engine)
            {
                Engine = null;
                await engine.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <param name="id">Which MQTT server the slot is for; empty for the WebSocket engine.</param>
    private sealed class EngineSlot<TOptions>(
        Func<TOptions, bool> isConfigured,
        Func<TOptions, IEntityFilter, ICommunicationEngine> create,
        string? id = null) : EngineSlot
        where TOptions : class
    {
        private string? _fingerprint;

        public string Id { get; } = id ?? string.Empty;

        /// <summary>What the running engine was created with.</summary>
        public TOptions? Options { get; private set; }

        /// <summary>Returns whether the engine was (re)created because its settings or the filter changed.</summary>
        public async Task<bool> ApplyAsync(TOptions options, EntityFilter filter, ILogger logger, CancellationToken cancellationToken)
        {
            var fingerprint = JsonSerializer.Serialize(options)
                + "|" + string.Join(',', filter.DisabledEntityIds.Order(StringComparer.Ordinal))
                + "|" + string.Join(',', filter.EnabledEntityIds.Order(StringComparer.Ordinal));
            if (fingerprint == _fingerprint)
            {
                return false;
            }

            if (Engine is { } previous)
            {
                LogRestarting(logger, previous.Name);
                await previous.StopAsync(cancellationToken).ConfigureAwait(false);
                await previous.DisposeAsync().ConfigureAwait(false);
            }

            var engine = create(options, filter);
            IsConfigured = isConfigured(options);
            Options = options;
            Engine = engine;
            _fingerprint = fingerprint;
            await engine.StartAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
    }
}
