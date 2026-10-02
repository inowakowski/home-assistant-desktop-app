using System.Collections.Concurrent;
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
/// <see cref="IEntityRegistry"/> and <see cref="IEventBus"/>; control clients (the settings window) query status,
/// settings and logs through <see cref="IServiceControl"/>.
/// </summary>
/// <remarks>
/// Any interactive user can connect, so clients are not trusted: sensor clients may only register sensors, may not
/// replace entities the service registered itself, and may only report readings for entities registered over IPC.
/// Saving settings and testing connections also require the client to be an elevated administrator.
/// </remarks>
public sealed partial class IpcServer(
    IEventBus bus,
    IEntityRegistry registry,
    IOptions<IpcOptions> options,
    ILogger<IpcServer> logger,
    IServiceControl? control = null) : BackgroundService
{
    private const int MaxClients = 8;
    private const int MaxClientNameLength = 64;
    private const int MaxConcurrentRequestsPerClient = 4;
    private const int MaxLogEntriesPerResponse = 50;

    private readonly ConcurrentDictionary<string, EntityOwner> _ipcEntityOwners = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, string> _sensorClients = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pipeName = options.Value.PipeName;
        var clients = new List<Task>();
        var isFirstInstance = true;
        var isPipeUnavailable = false;
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
        }
    }

    private async Task ServeClientAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await using (pipe.ConfigureAwait(false))
        {
            var stream = new IpcMessageStream(pipe);
            var connectionId = Guid.NewGuid();
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
                var isElevatedAdministrator = hello.Role == IpcClientRole.Control && IsElevatedAdministrator(pipe);
                LogClientConnected(logger, clientName, hello.Role, isElevatedAdministrator);
                if (hello.Role == IpcClientRole.Sensors)
                {
                    _sensorClients[connectionId] = clientName;
                }

                while (await stream.ReadAsync(cancellationToken).ConfigureAwait(false) is { } message)
                {
                    switch (message)
                    {
                        case EntityRegistrationMessage registration when hello.Role == IpcClientRole.Sensors:
                            await RegisterAsync(registration.Entity, new EntityOwner(connectionId, clientName), cancellationToken)
                                .ConfigureAwait(false);
                            break;
                        case TelemetryMessage telemetry when hello.Role == IpcClientRole.Sensors:
                            await PublishAsync(telemetry.Reading, clientName, cancellationToken).ConfigureAwait(false);
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
                _sensorClients.TryRemove(connectionId, out _);
                await MarkEntitiesUnavailableAsync(connectionId, cancellationToken).ConfigureAwait(false);

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
                    request.RequestId, AddIpcDetails(await control.GetStatusAsync(cancellationToken).ConfigureAwait(false))),
                GetSettingsRequest => new SettingsResponse(
                    request.RequestId, await control.GetSettingsAsync(cancellationToken).ConfigureAwait(false)),
                GetLogsRequest logs => new LogsResponse(
                    request.RequestId, control.GetLogs(logs.AfterSequence, MaxLogEntriesPerResponse)),
                SaveSettingsRequest save => await SaveSettingsAsync(save, clientName, cancellationToken).ConfigureAwait(false),
                TestConnectionRequest test => new OperationResponse(
                    request.RequestId, await control.TestConnectionAsync(test.Target, test.Settings, cancellationToken).ConfigureAwait(false)),
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
    private ServiceStatus AddIpcDetails(ServiceStatus status) => status with
    {
        SensorClients = [.. _sensorClients.Values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
        Entities =
        [
            .. status.Entities.Select(entity =>
                _ipcEntityOwners.TryGetValue(entity.Entity.Id, out var owner) ? entity with { Source = owner.ClientName } : entity),
        ],
    };

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

    private async Task RegisterAsync(EntityDescriptor entity, EntityOwner owner, CancellationToken cancellationToken)
    {
        if (!entity.Kind.ReportsState())
        {
            // Commands cannot be routed back to a client yet.
            LogRegistrationRejected(logger, owner.ClientName, entity.Id, "only sensors can be registered over IPC");
            return;
        }

        if (registry.TryGet(entity.Id, out _) && !_ipcEntityOwners.ContainsKey(entity.Id))
        {
            LogRegistrationRejected(logger, owner.ClientName, entity.Id, "the id belongs to a service entity");
            return;
        }

        var isNew = _ipcEntityOwners.TryAdd(entity.Id, owner);
        if (!isNew)
        {
            _ipcEntityOwners[entity.Id] = owner;
        }

        try
        {
            await registry.RegisterAsync(entity, cancellationToken).ConfigureAwait(false);
            await registry.SetAvailabilityAsync(entity.Id, isAvailable: true, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            if (isNew)
            {
                _ipcEntityOwners.TryRemove(entity.Id, out _);
            }

            LogRegistrationRejected(logger, owner.ClientName, entity.Id, ex.Message);
        }
    }

    /// <summary>
    /// A client that went away can no longer report, so its sensors must not keep showing their last value:
    /// a "user active" that stays on after sign-out would mislead every automation that reads it.
    /// </summary>
    private async Task MarkEntitiesUnavailableAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        // When the service itself is stopping, the engines report everything unavailable anyway.
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        foreach (var (entityId, owner) in _ipcEntityOwners)
        {
            // Another connection of the same user may have taken the entity over since.
            if (owner.ConnectionId == connectionId)
            {
                await registry.SetAvailabilityAsync(entityId, isAvailable: false, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task PublishAsync(TelemetryEvent reading, string clientName, CancellationToken cancellationToken)
    {
        if (!_ipcEntityOwners.ContainsKey(reading.SensorId))
        {
            LogReadingRejected(logger, clientName, reading.SensorId);
            return;
        }

        await bus.PublishAsync(reading with { Source = clientName }, cancellationToken).ConfigureAwait(false);
    }

    private sealed record EntityOwner(Guid ConnectionId, string ClientName);

    [LoggerMessage(Level = LogLevel.Error, Message = "Cannot create pipe '{PipeName}', so the tray app and the settings window cannot reach this service. Is another copy of the HADA service running? Trying again until it works.")]
    private static partial void LogPipeUnavailable(ILogger logger, Exception exception, string pipeName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pipe '{PipeName}' is available again; the tray app and the settings window can connect.")]
    private static partial void LogPipeAvailable(ILogger logger, string pipeName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dropped an IPC client that did not start with a supported hello message.")]
    private static partial void LogHandshakeRejected(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "IPC client '{ClientName}' connected ({Role}; elevated administrator: {IsElevatedAdministrator}).")]
    private static partial void LogClientConnected(ILogger logger, string clientName, IpcClientRole role, bool isElevatedAdministrator);

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
