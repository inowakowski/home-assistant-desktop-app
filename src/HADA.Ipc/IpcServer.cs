using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using System.Security;
using System.Security.Principal;
using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HADA.Ipc;

/// <summary>
/// Service side of the session IPC. Sensor clients (the tray) stream entities and readings into the service's
/// <see cref="IEntityRegistry"/> and <see cref="IEventBus"/>, and receive the commands Home Assistant sends to their
/// entities; control clients (the settings window) query status, settings and logs through <see cref="IServiceControl"/>.
/// </summary>
/// <remarks>
/// Any interactive user can connect, so clients are not trusted: sensor clients may not replace entities the
/// service registered itself, and may only report readings for entities they registered.
/// Saving settings and testing connections also require the client to be an elevated administrator.
/// <para>
/// With several users signed in, every user's tray connects and registers the same entities. Only one of them is
/// reported to Home Assistant at a time: the tray of the session on the computer's own screen, or else of a
/// session somebody is connected to remotely. The others are remembered, and take over when their user comes back.
/// </para>
/// </remarks>
[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The semaphore's wait handle is never used, so it holds nothing to release.")]
public sealed partial class IpcServer(
    IEventBus bus,
    IEntityRegistry registry,
    IOptions<IpcOptions> options,
    ILogger<IpcServer> logger,
    IServiceControl? control = null,
    ISessionDirectory? sessions = null) : BackgroundService
{
    private const int MaxClients = 8;
    private const int MaxClientNameLength = 64;
    private const int MaxConcurrentRequestsPerClient = 4;
    private const int MaxLogEntriesPerResponse = 50;
    private const int MaxEntitiesPerClient = 128;

    private readonly ISessionDirectory _sessions = sessions ?? new WindowsSessionDirectory();

    // Guards everything below: which trays are connected, what they registered, and which one is reported.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<SensorClient> _sensorClients = [];
    private readonly Dictionary<string, string> _ipcEntitySources = new(StringComparer.Ordinal);

    // The latest of each command that describes a state rather than an action; told to every tray that takes over.
    private readonly Dictionary<string, ActionCommand> _stickyCommands = new(StringComparer.Ordinal);
    private SensorClient? _active;
    private long _connections;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pipeName = options.Value.PipeName;
        var clients = new List<Task>();
        var isFirstInstance = true;
        var isPipeUnavailable = false;

        // Subscribed before the first client can connect, so no command is missed.
        var commands = bus.Subscribe<ActionCommand>();
        var background = Task.WhenAll(
            Task.Run(() => ForwardCommandsAsync(commands, stoppingToken), CancellationToken.None),
            Task.Run(() => WatchSessionsAsync(stoppingToken), CancellationToken.None));
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                clients.RemoveAll(client => client.IsCompleted);
                if (clients.Count >= MaxClients)
                {
                    await Task.WhenAny(clients).WaitAsync(stoppingToken).ConfigureAwait(false);
                    continue;
                }

                NamedPipeServerStream pipe;
                try
                {
                    // One extra instance so there is always one waiting for the next connection.
                    pipe = IpcPipeSecurity.CreateServerPipe(pipeName, MaxClients + 1, isFirstInstance);
                    isFirstInstance = false;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Most likely another process created the pipe first, e.g. a second copy of the service.
                    // Never share it, but keep trying: without the pipe the tray and the settings window cannot
                    // reach this service, and the other process may go away.
                    if (!isPipeUnavailable)
                    {
                        LogPipeUnavailable(logger, ex, pipeName);
                        isPipeUnavailable = true;
                    }

                    await Task.Delay(options.Value.PipeRetryDelay, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                if (isPipeUnavailable)
                {
                    LogPipeAvailable(logger, pipeName);
                    isPipeUnavailable = false;
                }

                try
                {
                    await pipe.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                }
                catch
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    throw;
                }

                clients.Add(Task.Run(() => ServeClientAsync(pipe, stoppingToken), CancellationToken.None));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await Task.WhenAll(clients).ConfigureAwait(false);
            await background.ConfigureAwait(false);
        }
    }

    /// <summary>Passes commands for a tray's entities, and the service's own requests to the session, on to the tray.</summary>
    private async Task ForwardCommandsAsync(IEventSubscription<ActionCommand> commands, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var command in commands.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var isSessionCommand = command.ActionId.StartsWith(SessionCommands.Prefix, StringComparison.Ordinal);
                SensorClient? target;
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (!isSessionCommand && !_ipcEntitySources.ContainsKey(command.ActionId))
                    {
                        // An entity of the service itself; its own handler takes care of it.
                        continue;
                    }

                    var isSticky = SessionCommands.IsSticky(command.ActionId);
                    if (isSticky)
                    {
                        _stickyCommands[command.ActionId] = command;
                    }

                    if (isSticky && _active is null)
                    {
                        // Nobody to tell yet; the first tray to connect is told.
                        continue;
                    }

                    target = _active is { } active && (isSessionCommand || active.Entities.ContainsKey(command.ActionId)) ? active : null;
                }
                finally
                {
                    _gate.Release();
                }

                if (target is null)
                {
                    LogNoClientForCommand(logger, command.ActionId);
                    continue;
                }

                await SendCommandAsync(target, command, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            await commands.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task SendCommandAsync(SensorClient target, ActionCommand command, CancellationToken cancellationToken)
    {
        try
        {
            await target.Stream.WriteAsync(new CommandMessage(command), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidDataException or OperationCanceledException)
        {
            LogCommandNotDelivered(logger, ex, command.ActionId, target.Name);
        }
    }

    /// <summary>Notices when another user's session comes to the front, which no tray reports by itself.</summary>
    private async Task WatchSessionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(options.Value.SessionCheckInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (_sensorClients.Count > 0)
                    {
                        await ReselectAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _gate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task ServeClientAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await using (pipe.ConfigureAwait(false))
        {
            var stream = new IpcMessageStream(pipe);
            SensorClient? client = null;
            var clientName = "unknown";
            var pendingRequests = new List<Task>();
            try
            {
                if (await stream.ReadAsync(cancellationToken).ConfigureAwait(false) is not HelloMessage hello
                    || hello.ProtocolVersion != IpcMessageStream.ProtocolVersion)
                {
                    LogHandshakeRejected(logger);
                    return;
                }

                clientName = hello.ClientName.Length > MaxClientNameLength ? hello.ClientName[..MaxClientNameLength] : hello.ClientName;

                // Checked once, since a connection's identity cannot change. The hello has been read, which impersonation requires.
                var isElevatedAdministrator = hello.Role == IpcClientRole.Control
                    && (IsElevatedAdministrator(pipe) || (options.Value.TrustSameUser && IsSameUser(pipe)));
                LogClientConnected(logger, clientName, hello.Role, isElevatedAdministrator);
                if (hello.Role == IpcClientRole.Sensors)
                {
                    client = new SensorClient(
                        clientName, _sessions.GetClientSessionId(pipe, clientName), stream, Interlocked.Increment(ref _connections));
                    await AddClientAsync(client, cancellationToken).ConfigureAwait(false);
                }

                while (await stream.ReadAsync(cancellationToken).ConfigureAwait(false) is { } message)
                {
                    switch (message)
                    {
                        case EntityRegistrationMessage registration when client is not null:
                            await RegisterAsync(client, registration.Entity, cancellationToken).ConfigureAwait(false);
                            break;
                        case TelemetryMessage telemetry when client is not null:
                            await PublishAsync(client, telemetry.Reading, cancellationToken).ConfigureAwait(false);
                            break;
                        case DeviceEventMessage happened when client is not null:
                            await PublishAsync(client, happened.Event, cancellationToken).ConfigureAwait(false);
                            break;
                        case IpcRequest request when hello.Role == IpcClientRole.Control:
                            // Answered concurrently so a slow connection test does not hold up status polling,
                            // but with a limit so one client cannot pile up unbounded work.
                            pendingRequests.RemoveAll(task => task.IsCompleted);
                            if (pendingRequests.Count >= MaxConcurrentRequestsPerClient)
                            {
                                await Task.WhenAny(pendingRequests).ConfigureAwait(false);
                            }

                            pendingRequests.Add(Task.Run(
                                () => AnswerAsync(stream, request, clientName, isElevatedAdministrator, cancellationToken),
                                CancellationToken.None));
                            break;
                        default:
                            LogUnexpectedMessage(logger, clientName, message.GetType().Name);
                            break;
                    }
                }

                LogClientDisconnected(logger, clientName);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                LogClientFailed(logger, ex, clientName);
            }
            finally
            {
                if (client is not null)
                {
                    await RemoveClientAsync(client, cancellationToken).ConfigureAwait(false);
                }

                // Let in-flight answers finish, or fail on the closed pipe, before the pipe is disposed.
                await Task.WhenAll(pendingRequests).ConfigureAwait(false);
            }
        }
    }

    private async Task AnswerAsync(
        IpcMessageStream stream, IpcRequest request, string clientName, bool isElevatedAdministrator, CancellationToken cancellationToken)
    {
        try
        {
            var response = await HandleRequestAsync(request, clientName, isElevatedAdministrator, cancellationToken).ConfigureAwait(false);
            try
            {
                await stream.WriteAsync(response, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException ex)
            {
                LogRequestFailed(logger, ex, request.GetType().Name);
                await stream.WriteAsync(
                        new ErrorResponse(request.RequestId, IpcError.Failed, "The response was too large to send."), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The client disconnected or the service is stopping.
        }
    }

    private async Task<IpcResponse> HandleRequestAsync(
        IpcRequest request, string clientName, bool isElevatedAdministrator, CancellationToken cancellationToken)
    {
        if (control is null)
        {
            return new ErrorResponse(request.RequestId, IpcError.NotSupported, "This service does not offer the control API.");
        }

        if (request is SaveSettingsRequest or TestConnectionRequest && !isElevatedAdministrator)
        {
            LogUnauthorizedRequest(logger, clientName, request.GetType().Name);
            return new ErrorResponse(request.RequestId, IpcError.Unauthorized, "Changing settings requires an elevated administrator.");
        }

        try
        {
            return request switch
            {
                GetStatusRequest => new StatusResponse(
                    request.RequestId,
                    await AddIpcDetailsAsync(await control.GetStatusAsync(cancellationToken).ConfigureAwait(false), cancellationToken)
                        .ConfigureAwait(false)),
                GetSettingsRequest => new SettingsResponse(
                    request.RequestId, await control.GetSettingsAsync(cancellationToken).ConfigureAwait(false)),
                GetLogsRequest logs => new LogsResponse(
                    request.RequestId, control.GetLogs(logs.AfterSequence, MaxLogEntriesPerResponse)),
                SaveSettingsRequest save => await SaveSettingsAsync(save, clientName, cancellationToken).ConfigureAwait(false),
                CheckForUpdateRequest => new UpdateCheckResponse(
                    request.RequestId, await control.CheckForUpdateAsync(cancellationToken).ConfigureAwait(false)),
                TestConnectionRequest test => new OperationResponse(
                    request.RequestId, await control.TestConnectionAsync(test.Target, test.Settings, test.ServerId, cancellationToken).ConfigureAwait(false)),
                _ => new ErrorResponse(request.RequestId, IpcError.NotSupported, $"Unsupported request '{request.GetType().Name}'."),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogRequestFailed(logger, ex, request.GetType().Name);
            return new ErrorResponse(request.RequestId, IpcError.Failed, "The request failed; see the service log for details.");
        }
    }

    private async Task<IpcResponse> SaveSettingsAsync(SaveSettingsRequest request, string clientName, CancellationToken cancellationToken)
    {
        LogSettingsSaveRequested(logger, clientName);
        return new OperationResponse(request.RequestId, await control!.SaveSettingsAsync(request.Settings, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Adds what only the IPC server knows: which trays are connected and which entities they registered.</summary>
    private async Task<ServiceStatus> AddIpcDetailsAsync(ServiceStatus status, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return status with
            {
                SensorClients = [.. _sensorClients.Select(client => client.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
                Entities =
                [
                    .. status.Entities.Select(entity =>
                        _ipcEntitySources.TryGetValue(entity.Entity.Id, out var source) ? entity with { Source = source } : entity),
                ],
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool IsElevatedAdministrator(NamedPipeServerStream pipe)
    {
        var isAdministrator = false;
        try
        {
            // Clients connect with identification-level impersonation, which is enough to inspect their token.
            pipe.RunAsClient(() =>
            {
                using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);

                // With UAC, an administrator's normal token only has the Administrators group as deny-only,
                // so this is true only for an elevated process.
                isAdministrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            // Cannot tell who the client is, so treat it as unprivileged.
        }

        return isAdministrator;
    }

    private static bool IsSameUser(NamedPipeServerStream pipe)
    {
        var isSameUser = false;
        try
        {
            using var server = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            pipe.RunAsClient(() =>
            {
                using var client = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
                isSameUser = client.User is not null && client.User == server.User;
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            // Cannot tell who the client is, so treat it as somebody else.
        }

        return isSameUser;
    }

    private async Task AddClientAsync(SensorClient client, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _sensorClients.Add(client);
            await ReselectAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// A client that went away can no longer report, so its sensors must not keep showing their last value:
    /// a "user active" that stays on after sign-out would mislead every automation that reads it.
    /// </summary>
    private async Task RemoveClientAsync(SensorClient client, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _sensorClients.Remove(client);

            // When the service itself is stopping, the engines report everything unavailable anyway.
            if (!cancellationToken.IsCancellationRequested)
            {
                await ReselectAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RegisterAsync(SensorClient client, EntityDescriptor entity, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (registry.TryGet(entity.Id, out _) && !_ipcEntitySources.ContainsKey(entity.Id))
            {
                LogRegistrationRejected(logger, client.Name, entity.Id, "the id belongs to a service entity");
                return;
            }

            if (!client.Entities.ContainsKey(entity.Id) && client.Entities.Count >= MaxEntitiesPerClient)
            {
                LogRegistrationRejected(logger, client.Name, entity.Id, "the client registered too many entities");
                return;
            }

            client.Entities[entity.Id] = entity;
            if (client == _active)
            {
                await ExposeAsync(client, entity, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task PublishAsync(SensorClient client, TelemetryEvent reading, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!client.Entities.ContainsKey(reading.SensorId))
            {
                LogReadingRejected(logger, client.Name, reading.SensorId);
                return;
            }

            // Kept even while another tray is the one reported, so this one's values are there when it takes over.
            reading = reading with { Source = client.Name };
            client.Readings[reading.SensorId] = reading;
            if (client == _active)
            {
                await bus.PublishAsync(reading, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task PublishAsync(SensorClient client, DeviceEvent deviceEvent, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Only what happens at the computer counts: a quick action of a user who was switched away from is nothing
            // Home Assistant should act on. Engines check that a quick action is one that exists.
            if (client != _active || !deviceEvent.IsWellFormed)
            {
                LogEventRejected(logger, client.Name, deviceEvent.Name);
                return;
            }

            await bus.PublishAsync(deviceEvent with { Source = client.Name }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Makes sure the right tray is the one reported, after a tray came or went or the session in use changed.</summary>
    private async Task ReselectAsync(CancellationToken cancellationToken)
    {
        var next = Choose();
        if (next == _active)
        {
            return;
        }

        _active = next;
        if (next is not null)
        {
            LogActiveClient(logger, next.Name, next.SessionId);
            foreach (var entity in next.Entities.Values.ToArray())
            {
                await ExposeAsync(next, entity, cancellationToken).ConfigureAwait(false);
            }

            foreach (var reading in next.Readings.Values)
            {
                if (next.Entities.ContainsKey(reading.SensorId))
                {
                    await bus.PublishAsync(reading, cancellationToken).ConfigureAwait(false);
                }
            }

            // Not awaited: a tray that is slow to read must not hold up everybody else's readings.
            foreach (var command in _stickyCommands.Values)
            {
                _ = SendCommandAsync(next, command, CancellationToken.None);
            }
        }

        foreach (var entityId in _ipcEntitySources.Keys)
        {
            if (next is null || !next.Entities.ContainsKey(entityId))
            {
                await registry.SetAvailabilityAsync(entityId, isAvailable: false, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The tray of the session on the computer's own screen; failing that, of a session somebody is connected to,
    /// i.e. over Remote Desktop. Among several, the one that connected last.
    /// </summary>
    private SensorClient? Choose()
    {
        var console = _sessions.ConsoleSessionId;
        SensorClient? connected = null;
        foreach (var client in _sensorClients.OrderByDescending(client => client.Order))
        {
            if (client.SessionId is { } session && session == console)
            {
                return client;
            }

            // A session that cannot be determined is given the benefit of the doubt.
            connected ??= client.SessionId is not { } id || _sessions.IsConnected(id) ? client : null;
        }

        return connected;
    }

    private async Task ExposeAsync(SensorClient client, EntityDescriptor entity, CancellationToken cancellationToken)
    {
        try
        {
            // Another user's tray may be another version, with the same id for a different kind of entity;
            // Home Assistant keeps kinds apart, so the old one has to go first.
            if (registry.TryGet(entity.Id, out var existing) && existing.Kind != entity.Kind)
            {
                await registry.UnregisterAsync(entity.Id, cancellationToken).ConfigureAwait(false);
            }

            await registry.RegisterAsync(entity, cancellationToken).ConfigureAwait(false);
            _ipcEntitySources[entity.Id] = client.Name;
            await registry.SetAvailabilityAsync(entity.Id, isAvailable: true, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            client.Entities.Remove(entity.Id);
            LogRegistrationRejected(logger, client.Name, entity.Id, ex.Message);
        }
    }

    /// <param name="order">Counts connections, so the latest one can be told.</param>
    private sealed class SensorClient(string name, uint? sessionId, IpcMessageStream stream, long order)
    {
        public string Name => name;

        public uint? SessionId => sessionId;

        public IpcMessageStream Stream => stream;

        public long Order => order;

        public Dictionary<string, EntityDescriptor> Entities { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, TelemetryEvent> Readings { get; } = new(StringComparer.Ordinal);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Ignored the event '{EventName}' from IPC client '{ClientName}': it is malformed, or the client is not the one at the computer.")]
    private static partial void LogEventRejected(ILogger logger, string clientName, string eventName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reporting the session entities of IPC client '{ClientName}' (session {SessionId}).")]
    private static partial void LogActiveClient(ILogger logger, string clientName, uint? sessionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Nothing was done about the command for '{ActionId}': the tray app that would carry it out is not running in the session in use.")]
    private static partial void LogNoClientForCommand(ILogger logger, string actionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The command for '{ActionId}' could not be sent to IPC client '{ClientName}'.")]
    private static partial void LogCommandNotDelivered(ILogger logger, Exception exception, string actionId, string clientName);

    [LoggerMessage(Level = LogLevel.Error, Message = "Cannot create pipe '{PipeName}', so the tray app and the settings window cannot reach this service. Is another copy of the HADA service running? Trying again until it works.")]
    private static partial void LogPipeUnavailable(ILogger logger, Exception exception, string pipeName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pipe '{PipeName}' is available again; the tray app and the settings window can connect.")]
    private static partial void LogPipeAvailable(ILogger logger, string pipeName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dropped an IPC client that did not start with a supported hello message.")]
    private static partial void LogHandshakeRejected(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "IPC client '{ClientName}' connected ({Role}; may change settings: {MayChangeSettings}).")]
    private static partial void LogClientConnected(ILogger logger, string clientName, IpcClientRole role, bool mayChangeSettings);

    [LoggerMessage(Level = LogLevel.Information, Message = "IPC client '{ClientName}' disconnected.")]
    private static partial void LogClientDisconnected(ILogger logger, string clientName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "IPC client '{ClientName}' failed and was disconnected.")]
    private static partial void LogClientFailed(ILogger logger, Exception exception, string clientName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "IPC client '{ClientName}' sent an unexpected {MessageType}.")]
    private static partial void LogUnexpectedMessage(ILogger logger, string clientName, string messageType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected entity '{EntityId}' from IPC client '{ClientName}': {Reason}.")]
    private static partial void LogRegistrationRejected(ILogger logger, string clientName, string entityId, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Ignored reading for '{SensorId}' from IPC client '{ClientName}': not registered over IPC.")]
    private static partial void LogReadingRejected(ILogger logger, string clientName, string sensorId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused {RequestType} from IPC client '{ClientName}': not an elevated administrator.")]
    private static partial void LogUnauthorizedRequest(ILogger logger, string clientName, string requestType);

    [LoggerMessage(Level = LogLevel.Information, Message = "IPC client '{ClientName}' is saving settings.")]
    private static partial void LogSettingsSaveRequested(ILogger logger, string clientName);

    [LoggerMessage(Level = LogLevel.Error, Message = "Handling {RequestType} failed.")]
    private static partial void LogRequestFailed(ILogger logger, Exception exception, string requestType);
}
