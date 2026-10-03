using System.IO.Pipes;

namespace HADA.Ipc;

/// <summary>
/// How the session pipe is kept to those who may use it, and who is at its other end. Each operating system has
/// its own notion of both.
/// </summary>
public interface IPipeAccess
{
    /// <summary>Creates a server instance that only the accounts meant to may connect to, and none may imitate.</summary>
    /// <param name="isFirstInstance">Whether creating must fail when a pipe of this name already exists.</param>
    NamedPipeServerStream CreateServerPipe(string pipeName, int maxInstances, bool isFirstInstance);

    /// <summary>Refuses a pipe that somebody other than the service may have created to collect what the client sends.</summary>
    /// <exception cref="UnauthorizedAccessException">The pipe's creator is not trusted.</exception>
    void EnsureTrustedServer(NamedPipeClientStream pipe);

    /// <summary>Whether the client may change the settings of an installed service. False when it cannot be told.</summary>
    bool IsElevatedAdministrator(NamedPipeServerStream pipe);

    /// <summary>Whether the client runs as the same user as this server. False when it cannot be told.</summary>
    bool IsSameUser(NamedPipeServerStream pipe);
}

public static class PipeAccess
{
    /// <summary>The rules of the operating system this process runs on.</summary>
    /// <exception cref="PlatformNotSupportedException">HADA has no pipe rules for this operating system yet.</exception>
    public static IPipeAccess Current { get; } = OperatingSystem.IsWindows()
        ? new WindowsPipeAccess()
        : throw new PlatformNotSupportedException("HADA's session pipe is not available on this operating system yet.");
}
