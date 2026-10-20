using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace HADA.Core.Hosting;

/// <summary>
/// A named signal between the processes of one user, such as "stop" or "bring your window to the front": one
/// process listens for it, any other may set it. On Windows it is a named event, as HADA always used; elsewhere a
/// Unix socket in a folder only the user can enter.
/// </summary>
public sealed class AppSignal : IDisposable
{
    // The longest path of a Unix socket is 104 bytes on macOS and 108 on Linux.
    private const int MaxSocketPath = 100;

    private readonly IDisposable _listener;

    private AppSignal(IDisposable listener) => _listener = listener;

    /// <summary>Starts listening for <paramref name="name"/>, e.g. <c>Local\HADA.Tray.Exit</c>.</summary>
    /// <param name="onSignal">Called on a thread of its own, not the caller's, each time the signal is set, or only the first time with <paramref name="once"/>.</param>
    public static AppSignal Listen(string name, Action onSignal, bool once = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(onSignal);
        return OperatingSystem.IsWindows()
            ? new AppSignal(new EventListener(name, onSignal, once))
            : new AppSignal(new SocketListener(SocketPath(DefaultFolder(), name), onSignal, once));
    }

    /// <summary>Sets the signal. Returns <see langword="false"/> when nobody listens for it.</summary>
    /// <exception cref="UnauthorizedAccessException">The listener runs with rights this process does not have, e.g. as administrator.</exception>
    public static bool TrySet(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return OperatingSystem.IsWindows() ? EventListener.TrySet(name) : SocketListener.TrySet(SocketPath(DefaultFolder(), name));
    }

    public void Dispose() => _listener.Dispose();

    /// <summary>Where the sockets of other systems live; see <see cref="AppFolders.Runtime"/>.</summary>
    internal static string DefaultFolder() => AppFolders.Runtime;

    /// <summary>The socket for <paramref name="name"/> in <paramref name="folder"/>; the <c>Local\</c> of Windows names is left out.</summary>
    internal static string SocketPath(string folder, string name)
    {
        var fileName = name.StartsWith(@"Local\", StringComparison.Ordinal) ? name[@"Local\".Length..] : name;
        var path = Path.Combine(folder, fileName + ".sock");
        if (Encoding.UTF8.GetByteCount(path) <= MaxSocketPath)
        {
            return path;
        }

        // Too long for a socket, as it soon is under the long temporary folder of macOS: a short name made from the whole one.
        return Path.Combine(folder, $"s{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fileName)), 0, 8)}.sock");
    }

    /// <summary>
    /// A thread of its own waits for the named event. It is not left to the thread pool, which signals "no longer
    /// waiting" before it has let go of the event, so that a listener just disposed could still be opened by name:
    /// this thread closes the event itself, and <see cref="Dispose"/> returns once it has.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private sealed class EventListener : IDisposable
    {
        private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

        private readonly EventWaitHandle _event;
        private readonly ManualResetEvent _stop = new(initialState: false);
        private readonly Thread _thread;
        private int _disposed;

        public EventListener(string name, Action onSignal, bool once)
        {
            _event = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, name);
            _thread = new Thread(() => Listen(onSignal, once)) { IsBackground = true, Name = "HADA signal " + name };
            _thread.Start();
        }

        public static bool TrySet(string name)
        {
            if (!EventWaitHandle.TryOpenExisting(name, out var signal))
            {
                return false;
            }

            using (signal)
            {
                return signal.Set();
            }
        }

        /// <summary>
        /// Stops listening and waits until the event is closed, so that once disposed, the signal's name leads nowhere.
        /// Waits for a signal being answered, too, unless disposed from its callback, which cannot wait for itself;
        /// the event is then closed as the callback returns.
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _stop.Set();
            if (Thread.CurrentThread != _thread)
            {
                _ = _thread.Join(StopTimeout);
            }
        }

        // The only one to close the event and the stop handle, which it does when asked to stop and not before: Dispose
        // sets the one and is done with it, and the thread waits for the other.
        private void Listen(Action onSignal, bool once)
        {
            try
            {
                // Stopping goes first: when both are set, the wait reports the lower index.
                WaitHandle[] handles = [_stop, _event];
                while (WaitHandle.WaitAny(handles) == 1)
                {
                    onSignal();
                    if (once)
                    {
                        // The signal is answered; it stays set up, as it does on a socket, until it is disposed.
                        _ = _stop.WaitOne();
                        return;
                    }
                }
            }
            finally
            {
                _event.Dispose();
                _stop.Dispose();
            }
        }
    }

    /// <summary>Each connection to the socket is one signal; nothing is sent over it.</summary>
    internal sealed class SocketListener : IDisposable
    {
        private readonly string _path;
        private readonly Socket _socket;
        private readonly CancellationTokenSource _stopping = new();

        public SocketListener(string path, Action onSignal, bool once)
        {
            _path = path;
            var folder = Path.GetDirectoryName(path)!;
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(folder);
            }
            else
            {
                Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            // Left behind by a listener that did not end tidily; whoever listens now owns the name.
            File.Delete(path);

            _socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                _socket.Bind(new UnixDomainSocketEndPoint(path));
                _socket.Listen(backlog: 8);
            }
            catch
            {
                _socket.Dispose();
                throw;
            }

            _ = AcceptAsync(onSignal, once, _stopping.Token);
        }

        public static bool TrySet(string path)
        {
            if (!File.Exists(path))
            {
                return false;
            }

            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                socket.Connect(new UnixDomainSocketEndPoint(path));
                return true;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AccessDenied)
            {
                throw new UnauthorizedAccessException($"The listener of {path} may not be signalled by this process.", ex);
            }
            catch (SocketException)
            {
                // A socket file nobody listens on any more.
                return false;
            }
        }

        public void Dispose()
        {
            _stopping.Cancel();
            _socket.Dispose();
            try
            {
                File.Delete(_path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The next listener deletes it.
            }

            _stopping.Dispose();
        }

        private async Task AcceptAsync(Action onSignal, bool once, CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    using (await _socket.AcceptAsync(cancellationToken).ConfigureAwait(false))
                    {
                    }

                    onSignal();
                    if (once)
                    {
                        return;
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // Disposed.
            }
        }
    }
}
