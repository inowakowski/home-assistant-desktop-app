using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.Versioning;
using HADA.Core;

namespace HADA.Ipc;

/// <summary>
/// The session pipe on Linux and macOS: a Unix socket in <see cref="AppFolders.Runtime"/>, a folder only the user
/// may enter. The service there is a process of that user, so whoever reaches the socket is that user.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal sealed class UnixPipeAccess : IPipeAccess
{
    private const UnixFileMode PrivateFolder = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>
    /// Where a pipe's socket lives: <paramref name="pipeName"/> itself when it is a path, as tests give, else a file
    /// of that name in the runtime folder.
    /// </summary>
    public static string SocketPath(string pipeName) =>
        Path.IsPathRooted(pipeName) ? pipeName : Path.Combine(AppFolders.Runtime, pipeName + ".sock");

    public NamedPipeServerStream CreateServerPipe(string pipeName, int maxInstances, bool isFirstInstance)
    {
        var path = SocketPath(pipeName);
        if (isFirstInstance)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!, PrivateFolder);

            // A socket left by a service that ended without tidying up would keep this one from starting.
            if (File.Exists(path))
            {
                if (IsServed(path))
                {
                    throw new IOException($"Another HADA service is using {path}.");
                }

                File.Delete(path);
            }
        }

        return new NamedPipeServerStream(path, PipeDirection.InOut, maxInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    }

    public NamedPipeClientStream CreateClientPipe(string pipeName) =>
        new(".", SocketPath(pipeName), PipeDirection.InOut, PipeOptions.Asynchronous);

    /// <summary>Refuses a socket outside a folder that only this user may enter, where somebody else could have made it.</summary>
    public void EnsureTrustedServer(NamedPipeClientStream pipe, string pipeName)
    {
        var folder = Path.GetDirectoryName(SocketPath(pipeName))!;
        if (File.GetUnixFileMode(folder) != PrivateFolder)
        {
            throw new UnauthorizedAccessException($"{folder} may be entered by other users; refusing to send session data through it.");
        }
    }

    // Settings are this user's own, as they are for a portable copy on Windows; there is no administrator window.
    public bool IsElevatedAdministrator(NamedPipeServerStream pipe) => false;

    // Only this user can reach the socket.
    public bool IsSameUser(NamedPipeServerStream pipe) => true;

    private static bool IsServed(string path)
    {
        try
        {
            using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            probe.Connect(new UnixDomainSocketEndPoint(path));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}

/// <summary>
/// The sessions of Linux and macOS as HADA sees them: one. The service there runs for one user and hears only
/// that user's agent, so it is always the one at the screen.
/// </summary>
internal sealed class SingleSessionDirectory : ISessionDirectory
{
    private const uint Session = 1;

    public uint? ConsoleSessionId => Session;

    public uint? GetClientSessionId(NamedPipeServerStream pipe, string clientName) => Session;

    public bool IsConnected(uint sessionId) => true;
}
