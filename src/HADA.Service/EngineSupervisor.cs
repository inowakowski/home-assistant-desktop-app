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
/// Owns the communication engines: one per MQTT server, and one per Home Assistant connected to through its
/// WebSocket API. Whenever an engine's settings or the set of disabled entities change, e.g. after settings are
/// saved from the window, that engine is stopped and recreated with the new values; a server that was removed is
/// disconnected from.
/// </summary>
public sealed partial class EngineSupervisor : IHostedService, IAsyncDisposable
{
    // Configuration reloads fire several change notifications in a row; apply them once.
    private static readonly TimeSpan ReloadDelay = TimeSpan.FromMilliseconds(500);

    private readonly IEventBus _bus;
    private readonly IEntityRegistry _registry;
    private readonly TelemetryCache? _telemetry;
    private readonly IMobileAppRegistrationStore _registrations;
    private readonly IOptionsMonitor<MqttOptions> _mqttOptions;
    private readonly IOptionsMonitor<MqttServersOptions> _mqttServers;
    private readonly IOptionsMonitor<HaWebSocketOptions> _homeAssistantOptions;
    private readonly IOptionsMonitor<HomeAssistantServersOptions> _homeAssistantServers;
    private readonly IOptionsMonitor<EntityOptions> _entityOptions;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _reloadLock = new();
    private readonly List<IDisposable> _changeRegistrations = [];

    // Replaced as a whole, so the status can be read while settings are applied.
    private volatile EngineSlot<MqttOptions>[] _mqtt = [];
    private volatile EngineSlot<HaWebSocketOptions>[] _homeAssistant = [];
    private CancellationTokenSource? _pendingReload;
    private bool _stopped;

    public EngineSupervisor(
        IEventBus bus,
        IEntityRegistry registry,
        IOptionsMonitor<MqttOptions> mqttOptions,
        IOptionsMonitor<MqttServersOptions> mqttServers,
        IOptionsMonitor<HaWebSocketOptions> homeAssistantOptions,
        IOptionsMonitor<HomeAssistantServersOptions> homeAssistantServers,
        IOptionsMonitor<EntityOptions> entityOptions,
        ILoggerFactory loggerFactory,
        TelemetryCache? telemetry = null,
        IMobileAppRegistrationStore? registrations = null)
    {
        _bus = bus;
        _registry = registry;
        _telemetry = telemetry;

        // Shared by the engines, and outliving them: an engine that is recreated must find its registration again.
        _registrations = registrations ?? new InMemoryMobileAppRegistrationStore();
        _mqttOptions = mqttOptions;
        _mqttServers = mqttServers;
        _homeAssistantOptions = homeAssistantOptions;
        _homeAssistantServers = homeAssistantServers;
        _entityOptions = entityOptions;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<EngineSupervisor>();
    }

    /// <summary>
    /// One entry per MQTT server, then one per Home Assistant connected to directly; of either kind a single
    /// unconfigured one when there is none.
    /// </summary>
    public IReadOnlyList<EngineStatus> GetStatus()
    {
        var mqtt = _mqtt;
        var homeAssistant = _homeAssistant;
        var statuses = new List<EngineStatus>(mqtt.Length + homeAssistant.Length);
        AddStatuses(statuses, MqttEngine.EngineName, mqtt, options => options.Name);
        AddStatuses(statuses, HaWebSocketEngine.EngineName, homeAssistant, options => options.Name);
        return statuses;
    }

    private static void AddStatuses<TOptions>(
        List<EngineStatus> statuses, string engineName, EngineSlot<TOptions>[] slots, Func<TOptions, string?> nameOf)
        where TOptions : class
    {
        var before = statuses.Count;
        foreach (var slot in slots)
        {
            if (slot is { Engine: { } engine, Options: { } options })
            {
                statuses.Add(new EngineStatus(engineName, slot.IsConfigured, engine.State, slot.Id, nameOf(options) ?? string.Empty, engine.Issue));
            }
        }

        if (statuses.Count == before)
        {
            statuses.Add(new EngineStatus(engineName, false, EngineConnectionState.Disconnected));
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await ApplyAsync(cancellationToken).ConfigureAwait(false);

        AddRegistration(_mqttOptions.OnChange(_ => ScheduleReload()));
        AddRegistration(_mqttServers.OnChange(_ => ScheduleReload()));
        AddRegistration(_homeAssistantOptions.OnChange(_ => ScheduleReload()));
        AddRegistration(_homeAssistantServers.OnChange(_ => ScheduleReload()));
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

    private IEnumerable<EngineSlot> AllSlots() => [.. _mqtt, .. _homeAssistant];

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
            anyRecreated |= await ApplyHomeAssistantAsync(filter, cancellationToken).ConfigureAwait(false);

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
        var (slots, anyRecreated) = await ApplyServersAsync(
                _mqtt,
                MqttServersOptions.Resolve(_mqttServers.CurrentValue, _mqttOptions.CurrentValue),
                server => server.Id!,
                id => new EngineSlot<MqttOptions>(
                    options => !string.IsNullOrWhiteSpace(options.Host),
                    (options, engineFilter) => new MqttEngine(
                        _bus, _registry, Options.Create(options), _loggerFactory.CreateLogger<MqttEngine>(), engineFilter),
                    id),
                removed => LogServerRemoved(_logger, removed.Name is { Length: > 0 } name ? name : removed.Host ?? removed.Id!),
                filter,
                cancellationToken)
            .ConfigureAwait(false);
        _mqtt = slots;
        return anyRecreated;
    }

    /// <summary>The same for the Home Assistants connected to through their WebSocket API.</summary>
    private async Task<bool> ApplyHomeAssistantAsync(EntityFilter filter, CancellationToken cancellationToken)
    {
        var (slots, anyRecreated) = await ApplyServersAsync(
                _homeAssistant,
                HomeAssistantServersOptions.Resolve(_homeAssistantServers.CurrentValue, _homeAssistantOptions.CurrentValue),
                server => server.Id!,
                id => new EngineSlot<HaWebSocketOptions>(
                    options => !string.IsNullOrWhiteSpace(options.BaseUrl) && !string.IsNullOrWhiteSpace(options.AccessToken),
                    (options, engineFilter) => new HaWebSocketEngine(
                        _bus, _registry, Options.Create(options), _loggerFactory.CreateLogger<HaWebSocketEngine>(), engineFilter, _registrations),
                    id),
                removed => LogHomeAssistantRemoved(_logger, removed.Name is { Length: > 0 } name ? name : removed.BaseUrl ?? removed.Id!),
                filter,
                cancellationToken)
            .ConfigureAwait(false);
        _homeAssistant = slots;
        return anyRecreated;
    }

    /// <summary>
    /// Stops the engines of servers that are no longer in the settings, and gives every server that is in them
    /// one: new, or recreated when its settings changed. Returns the slots in the order of the servers.
    /// </summary>
    private async Task<(EngineSlot<TOptions>[] Slots, bool AnyRecreated)> ApplyServersAsync<TOptions>(
        EngineSlot<TOptions>[] current,
        IReadOnlyList<TOptions> servers,
        Func<TOptions, string> idOf,
        Func<string, EngineSlot<TOptions>> createSlot,
        Action<TOptions> logRemoved,
        EntityFilter filter,
        CancellationToken cancellationToken)
        where TOptions : class
    {
        var anyRecreated = false;
        foreach (var removed in current.Where(slot => servers.All(server => idOf(server) != slot.Id)))
        {
            if (removed.Options is { } options)
            {
                logRemoved(options);
            }

            await removed.StopAsync(cancellationToken).ConfigureAwait(false);
            await removed.DisposeAsync().ConfigureAwait(false);
        }

        var next = new List<EngineSlot<TOptions>>(servers.Count);
        foreach (var server in servers)
        {
            var slot = Array.Find(current, slot => slot.Id == idOf(server)) ?? createSlot(idOf(server));
            anyRecreated |= await slot.ApplyAsync(server, filter, _logger, cancellationToken).ConfigureAwait(false);
            next.Add(slot);
        }

        return ([.. next], anyRecreated);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Settings changed; restarting the {EngineName} engine.")]
    private static partial void LogRestarting(ILogger logger, string engineName);

    [LoggerMessage(Level = LogLevel.Information, Message = "The MQTT server '{Server}' was removed from the settings; disconnected from it.")]
    private static partial void LogServerRemoved(ILogger logger, string server);

    [LoggerMessage(Level = LogLevel.Information, Message = "The Home Assistant '{Server}' was removed from the settings; disconnected from it.")]
    private static partial void LogHomeAssistantRemoved(ILogger logger, string server);

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

    /// <param name="id">Which server the slot is for.</param>
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
