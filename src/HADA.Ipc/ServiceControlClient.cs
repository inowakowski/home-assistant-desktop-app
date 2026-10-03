using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Principal;

namespace HADA.Ipc;

/// <summary>
/// Request/response client for the service's control API, used by the settings window. Connects on first use and again
/// after the service restarts; while the service cannot be reached, calls fail with <see cref="ServiceUnavailableException"/>.
/// </summary>
public sealed class ServiceControlClient(IpcOptions? options = null) : IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    // Connection tests can take a handshake timeout of their own on the service side.
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly IpcOptions _options = options ?? new IpcOptions();
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<IpcResponse>> _pending = new();
    private volatile Connection? _connection;
    private volatile bool _disposed;

    public async Task<ServiceStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        Expect<StatusResponse>(await SendAsync(new GetStatusRequest(Guid.NewGuid()), cancellationToken).ConfigureAwait(false)).Status;

    public async Task<SettingsSnapshot> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        Expect<SettingsResponse>(await SendAsync(new GetSettingsRequest(Guid.NewGuid()), cancellationToken).ConfigureAwait(false)).Settings;

    /// <exception cref="ServiceControlException">With <see cref="IpcError.Unauthorized"/> unless running as an elevated administrator.</exception>
    public async Task<OperationResult> SaveSettingsAsync(SettingsUpdate settings, CancellationToken cancellationToken = default) =>
        Expect<OperationResponse>(await SendAsync(new SaveSettingsRequest(Guid.NewGuid(), settings), cancellationToken).ConfigureAwait(false)).Result;

    /// <exception cref="ServiceControlException">With <see cref="IpcError.Unauthorized"/> unless running as an elevated administrator.</exception>
    /// <param name="serverId">Which server of <paramref name="settings"/> to test; the first one of that kind when <see langword="null"/>.</param>
    public async Task<OperationResult> TestConnectionAsync(
        ConnectionTarget target, SettingsUpdate settings, string? serverId = null, CancellationToken cancellationToken = default) =>
        Expect<OperationResponse>(
            await SendAsync(new TestConnectionRequest(Guid.NewGuid(), target, settings, serverId), cancellationToken)
                .ConfigureAwait(false)).Result;

    /// <param name="afterSequence">0 for the most recent entries; otherwise the last sequence number already received.</param>
    public async Task<IReadOnlyList<LogEntry>> GetLogsAsync(long afterSequence, CancellationToken cancellationToken = default) =>
        Expect<LogsResponse>(await SendAsync(new GetLogsRequest(Guid.NewGuid(), afterSequence), cancellationToken).ConfigureAwait(false)).Entries;

    /// <summary>Makes the service look for a newer version now; the answer is also part of the status from then on.</summary>
    public async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default) =>
        Expect<UpdateCheckResponse>(await SendAsync(new CheckForUpdateRequest(Guid.NewGuid()), cancellationToken).ConfigureAwait(false)).Result;

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        if (_connection is { } connection)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<IpcResponse> SendAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        var response = new TaskCompletionSource<IpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[request.RequestId] = response;
        try
        {
            // Checked after registering, so a connection that dies right now also fails this request.
            if (connection.IsClosed)
            {
                throw new ServiceUnavailableException();
            }

            await connection.Stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);
            return await response.Task.WaitAsync(RequestTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw new ServiceUnavailableException(inner: ex);
        }
        catch (TimeoutException ex)
        {
            throw new ServiceUnavailableException("The HADA service did not answer in time.", ex);
        }
        finally
        {
            _pending.TryRemove(request.RequestId, out _);
        }
    }

    private async Task<Connection> GetConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connection is { IsClosed: false } current)
        {
            return current;
        }

        await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connection is { IsClosed: false } existing)
            {
                return existing;
            }

            // Identification only: the service may check who we are, but cannot act as us.
            var pipe = new NamedPipeClientStream(
                ".", _options.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            try
            {
                await pipe.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
                PipeAccess.Current.EnsureTrustedServer(pipe);

                var stream = new IpcMessageStream(pipe);
                await stream.WriteAsync(
                        new HelloMessage(IpcMessageStream.ProtocolVersion, _options.ClientName, IpcClientRole.Control), cancellationToken)
                    .ConfigureAwait(false);

                var connection = new Connection(pipe, stream);
                connection.StartReading(OnResponse, OnClosed);
                _connection = connection;
                return connection;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw new ServiceUnavailableException(inner: ex);
            }
            catch
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private void OnResponse(IpcResponse response)
    {
        if (_pending.TryGetValue(response.RequestId, out var pending))
        {
            pending.TrySetResult(response);
        }
    }

    private void OnClosed()
    {
        foreach (var pending in _pending.Values)
        {
            pending.TrySetException(new ServiceUnavailableException());
        }
    }

    private static T Expect<T>(IpcResponse response)
        where T : IpcResponse => response switch
        {
            T expected => expected,
            ErrorResponse error => throw new ServiceControlException(error.Error, error.Message),
            _ => throw new InvalidDataException($"Unexpected response '{response.GetType().Name}' from the HADA service."),
        };

    private sealed class Connection(NamedPipeClientStream pipe, IpcMessageStream stream) : IAsyncDisposable
    {
        private volatile bool _isClosed;

        public IpcMessageStream Stream => stream;

        public bool IsClosed => _isClosed;

        public void StartReading(Action<IpcResponse> onResponse, Action onClosed) =>
            _ = Task.Run(async () =>
            {
                try
                {
                    while (await stream.ReadAsync(CancellationToken.None).ConfigureAwait(false) is { } message)
                    {
                        if (message is IpcResponse response)
                        {
                            onResponse(response);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException)
                {
                    // The service went away or sent garbage; either way this connection is finished.
                }
                finally
                {
                    _isClosed = true;
                    onClosed();
                }
            });

        public ValueTask DisposeAsync()
        {
            _isClosed = true;
            return pipe.DisposeAsync();
        }
    }
}

/// <summary>The HADA service is not running, cannot be reached, or did not answer in time.</summary>
public sealed class ServiceUnavailableException(string? message = null, Exception? inner = null)
    : Exception(message ?? "The HADA service is not running or cannot be reached.", inner);

/// <summary>The service refused or could not handle a request.</summary>
public sealed class ServiceControlException(IpcError error, string message) : Exception(message)
{
    public IpcError Error { get; } = error;
}
