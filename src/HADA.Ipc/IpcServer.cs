using System.Collections.Concurrent;
using System.IO.Pipes;
using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HADA.Ipc;

/// <summary>
/// Service side of the session IPC: accepts tray clients over a named pipe and feeds their entities and readings
/// into the service's <see cref="IEntityRegistry"/> and <see cref="IEventBus"/>.
/// </summary>
/// <remarks>
/// Any interactive user can connect, so clients are not trusted: they may only register sensors, may not replace
/// entities the service registered itself, and may only report readings for entities registered over IPC.
/// </remarks>
public sealed partial class IpcServer(
    IEventBus bus, IEntityRegistry registry, IOptions<IpcOptions> options, ILogger<IpcServer> logger) : BackgroundService
{
    private const int MaxClients = 4;
    private const int MaxClientNameLength = 64;

    private readonly ConcurrentDictionary<string, byte> _ipcEntityIds = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pipeName = options.Value.PipeName;
        var clients = new List<Task>();
        var isFirstInstance = true;
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
                    // Most likely another process created the pipe first; refuse to share it.
                    LogPipeUnavailable(logger, ex, pipeName);
                    return;
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
            var clientName = "unknown";
            try
            {
                if (await stream.ReadAsync(cancellationToken).ConfigureAwait(false) is not HelloMessage hello
                    || hello.ProtocolVersion != IpcMessageStream.ProtocolVersion)
                {
                    LogHandshakeRejected(logger);
                    return;
                }

                clientName = hello.ClientName.Length > MaxClientNameLength ? hello.ClientName[..MaxClientNameLength] : hello.ClientName;
                LogClientConnected(logger, clientName);

                while (await stream.ReadAsync(cancellationToken).ConfigureAwait(false) is { } message)
                {
                    switch (message)
                    {
                        case EntityRegistrationMessage registration:
                            await RegisterAsync(registration.Entity, clientName, cancellationToken).ConfigureAwait(false);
                            break;
                        case TelemetryMessage telemetry:
                            await PublishAsync(telemetry.Reading, clientName, cancellationToken).ConfigureAwait(false);
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
        }
    }

    private async Task RegisterAsync(EntityDescriptor entity, string clientName, CancellationToken cancellationToken)
    {
        if (entity.Kind != EntityKind.Sensor)
        {
            // Commands cannot be routed back to a client yet.
            LogRegistrationRejected(logger, clientName, entity.Id, "only sensors can be registered over IPC");
            return;
        }

        if (registry.TryGet(entity.Id, out _) && !_ipcEntityIds.ContainsKey(entity.Id))
        {
            LogRegistrationRejected(logger, clientName, entity.Id, "the id belongs to a service entity");
            return;
        }

        var isNew = _ipcEntityIds.TryAdd(entity.Id, 0);
        try
        {
            await registry.RegisterAsync(entity, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            if (isNew)
            {
                _ipcEntityIds.TryRemove(entity.Id, out _);
            }

            LogRegistrationRejected(logger, clientName, entity.Id, ex.Message);
        }
    }

    private async Task PublishAsync(TelemetryEvent reading, string clientName, CancellationToken cancellationToken)
    {
        if (!_ipcEntityIds.ContainsKey(reading.SensorId))
        {
            LogReadingRejected(logger, clientName, reading.SensorId);
            return;
        }

        await bus.PublishAsync(reading with { Source = clientName }, cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Session IPC disabled: cannot create pipe '{PipeName}'.")]
    private static partial void LogPipeUnavailable(ILogger logger, Exception exception, string pipeName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dropped an IPC client that did not start with a supported hello message.")]
    private static partial void LogHandshakeRejected(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "IPC client '{ClientName}' connected.")]
    private static partial void LogClientConnected(ILogger logger, string clientName);

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
}
