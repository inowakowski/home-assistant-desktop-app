using System.Collections.Concurrent;
using System.IO.Pipes;
using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Hosting;
using HADA.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HADA.Ipc;

/// <summary>
/// Tray side of the session IPC: streams entities and readings from the local event bus to the service, and
/// publishes the commands the service passes on to the local event bus, where the tray's actions pick them up.
/// Survives service restarts by reconnecting and replaying the registry and the latest reading of each sensor.
/// </summary>
public sealed partial class IpcClient(
    IEventBus bus, IEntityRegistry registry, IOptions<IpcOptions> options, ILogger<IpcClient> logger) : EagerBackgroundService
{
    private readonly ConcurrentDictionary<string, TelemetryEvent> _lastReadings = new(StringComparer.Ordinal);

    // Guards _session so a replay on connect and live forwarding never interleave.
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private IpcMessageStream? _session;

    public bool IsConnected => Volatile.Read(ref _session) is not null;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // One subscription for both event types keeps every registration ahead of the readings that follow it,
        // which the service requires. It is taken before the first await, i.e. before sensors start.
        var events = bus.Subscribe<object>();
        await using (events.ConfigureAwait(false))
        {
            var forwarding = Task.Run(() => ForwardEventsAsync(events, stoppingToken), CancellationToken.None);
            await MaintainConnectionAsync(stoppingToken).ConfigureAwait(false);
            await forwarding.ConfigureAwait(false);
        }
    }

    private async Task MaintainConnectionAsync(CancellationToken cancellationToken)
    {
        var retryDelay = options.Value.MinReconnectDelay;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Identification only: the service may check who we are, but cannot act as us.
                var pipe = new NamedPipeClientStream(
                    ".",
                    options.Value.PipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous,
                    System.Security.Principal.TokenImpersonationLevel.Identification);
                await using (pipe.ConfigureAwait(false))
                {
                    // Waits for as long as it takes the service to create the pipe.
                    await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
                    PipeAccess.Current.EnsureTrustedServer(pipe);

                    var stream = new IpcMessageStream(pipe);
                    await OpenSessionAsync(stream, cancellationToken).ConfigureAwait(false);
                    LogConnected(logger, options.Value.PipeName);
                    retryDelay = options.Value.MinReconnectDelay;

                    try
                    {
                        // Reading also notices promptly when the service goes away.
                        while (await stream.ReadAsync(cancellationToken).ConfigureAwait(false) is { } message)
                        {
                            if (message is CommandMessage command)
                            {
                                // Home Assistant wants something done by one of this session's actions.
                                await bus.PublishAsync(command.Command, cancellationToken).ConfigureAwait(false);
                            }
                        }
                    }
                    finally
                    {
                        await CloseSessionAsync().ConfigureAwait(false);
                    }

                    LogDisconnected(logger, retryDelay);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                LogConnectionFailed(logger, ex, retryDelay);
            }

            try
            {
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            retryDelay = TimeSpan.FromTicks(Math.Min(retryDelay.Ticks * 2, options.Value.MaxReconnectDelay.Ticks));
        }
    }

    private async Task OpenSessionAsync(IpcMessageStream stream, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(new HelloMessage(IpcMessageStream.ProtocolVersion, options.Value.ClientName), cancellationToken)
                .ConfigureAwait(false);

            // Entities first: the service ignores readings for sensors it has not seen registered.
            foreach (var entity in registry.Entities)
            {
                await stream.WriteAsync(new EntityRegistrationMessage(entity), cancellationToken).ConfigureAwait(false);
            }

            foreach (var reading in _lastReadings.Values)
            {
                await stream.WriteAsync(new TelemetryMessage(reading), cancellationToken).ConfigureAwait(false);
            }

            Volatile.Write(ref _session, stream);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task CloseSessionAsync()
    {
        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            Volatile.Write(ref _session, null);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ForwardEventsAsync(IEventSubscription<object> events, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var @event in events.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                IpcMessage message;
                switch (@event)
                {
                    case TelemetryEvent reading:
                        _lastReadings[reading.SensorId] = reading;
                        message = new TelemetryMessage(reading);
                        break;
                    case EntityRegistered registration:
                        message = new EntityRegistrationMessage(registration.Entity);
                        break;
                    case DeviceEvent deviceEvent:
                        // Of the moment: not remembered, and not sent later if the service cannot be reached now.
                        message = new DeviceEventMessage(deviceEvent);
                        break;
                    default:
                        continue;
                }

                await SendIfConnectedAsync(message, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task SendIfConnectedAsync(IpcMessage message, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // While disconnected nothing is lost: the registry and cached readings are replayed on connect.
            if (_session is { } session)
            {
                await session.WriteAsync(message, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (IOException ex)
        {
            // The connection loop sees the broken pipe as well and reconnects.
            LogSendFailed(logger, ex);
            Volatile.Write(ref _session, null);
        }
        catch (InvalidDataException ex)
        {
            LogMessageDropped(logger, ex);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Connected to the service over pipe '{PipeName}'.")]
    private static partial void LogConnected(ILogger logger, string pipeName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The service closed the IPC connection; reconnecting in {RetryDelay}.")]
    private static partial void LogDisconnected(ILogger logger, TimeSpan retryDelay);

    [LoggerMessage(Level = LogLevel.Warning, Message = "IPC connection to the service failed; retrying in {RetryDelay}.")]
    private static partial void LogConnectionFailed(ILogger logger, Exception exception, TimeSpan retryDelay);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Sending to the service failed.")]
    private static partial void LogSendFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dropped a message that cannot be sent to the service.")]
    private static partial void LogMessageDropped(ILogger logger, Exception exception);
}
