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
/// Owns the communication engines. Whenever an engine's settings or the set of disabled entities change, e.g. after
/// settings are saved from the window, that engine is stopped and recreated with the new values.
/// </summary>
public sealed partial class EngineSupervisor : IHostedService, IAsyncDisposable
{
    // Configuration reloads fire several change notifications in a row; apply them once.
    private static readonly TimeSpan ReloadDelay = TimeSpan.FromMilliseconds(500);

    private readonly IOptionsMonitor<EntityOptions> _entityOptions;
    private readonly ILogger _logger;
    private readonly EngineSlot[] _slots;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _reloadLock = new();
    private readonly List<IDisposable> _changeRegistrations = [];
    private CancellationTokenSource? _pendingReload;
    private bool _stopped;

    public EngineSupervisor(
        IEventBus bus,
        IEntityRegistry registry,
        IOptionsMonitor<MqttOptions> mqttOptions,
        IOptionsMonitor<HaWebSocketOptions> homeAssistantOptions,
        IOptionsMonitor<EntityOptions> entityOptions,
        ILoggerFactory loggerFactory)
    {
        _entityOptions = entityOptions;
        _logger = loggerFactory.CreateLogger<EngineSupervisor>();
        _slots =
        [
            new EngineSlot<MqttOptions>(
                mqttOptions,
                options => !string.IsNullOrWhiteSpace(options.Host),
                (options, filter) => new MqttEngine(
                    bus, registry, Options.Create(options), loggerFactory.CreateLogger<MqttEngine>(), filter)),
            new EngineSlot<HaWebSocketOptions>(
                homeAssistantOptions,
                options => !string.IsNullOrWhiteSpace(options.BaseUrl) && !string.IsNullOrWhiteSpace(options.AccessToken),
                (options, filter) => new HaWebSocketEngine(
                    bus, registry, Options.Create(options), loggerFactory.CreateLogger<HaWebSocketEngine>(), filter)),
        ];
    }

    public IReadOnlyList<EngineStatus> GetStatus()
    {
        var statuses = new List<EngineStatus>(_slots.Length);
        foreach (var slot in _slots)
        {
            if (slot.Engine is { } engine)
            {
                statuses.Add(new EngineStatus(engine.Name, slot.IsConfigured, engine.State));
            }
        }

        return statuses;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await ApplyAsync(cancellationToken).ConfigureAwait(false);

        foreach (var slot in _slots)
        {
            AddRegistration(slot.OnChange(ScheduleReload));
        }

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

            foreach (var slot in _slots)
            {
                await slot.StopAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var slot in _slots)
        {
            await slot.DisposeAsync().ConfigureAwait(false);
        }
    }

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

            var filter = new EntityFilter(_entityOptions.CurrentValue.Disabled);
            foreach (var slot in _slots)
            {
                await slot.ApplyAsync(filter, _logger, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Settings changed; restarting the {EngineName} engine.")]
    private static partial void LogRestarting(ILogger logger, string engineName);

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

        public abstract IDisposable? OnChange(Action callback);

        public abstract Task ApplyAsync(EntityFilter filter, ILogger logger, CancellationToken cancellationToken);

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

    private sealed class EngineSlot<TOptions>(
        IOptionsMonitor<TOptions> monitor,
        Func<TOptions, bool> isConfigured,
        Func<TOptions, IEntityFilter, ICommunicationEngine> create) : EngineSlot
        where TOptions : class
    {
        private string? _fingerprint;

        public override IDisposable? OnChange(Action callback) => monitor.OnChange(_ => callback());

        public override async Task ApplyAsync(EntityFilter filter, ILogger logger, CancellationToken cancellationToken)
        {
            var options = monitor.CurrentValue;
            var fingerprint = JsonSerializer.Serialize(options) + "|"
                + string.Join(',', filter.DisabledEntityIds.Order(StringComparer.Ordinal));
            if (fingerprint == _fingerprint)
            {
                return;
            }

            if (Engine is { } previous)
            {
                LogRestarting(logger, previous.Name);
                await previous.StopAsync(cancellationToken).ConfigureAwait(false);
                await previous.DisposeAsync().ConfigureAwait(false);
            }

            var engine = create(options, filter);
            IsConfigured = isConfigured(options);
            Engine = engine;
            _fingerprint = fingerprint;
            await engine.StartAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
