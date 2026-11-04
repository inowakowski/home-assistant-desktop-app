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

    /// <summary>Creates the client end, which the service may check the identity of but cannot act as.</summary>
    NamedPipeClientStream CreateClientPipe(string pipeName);

    /// <summary>
    /// Refuses a pipe that somebody other than the service may have created, to collect what the client sends or to
    /// give it commands to carry out.
    /// </summary>
    /// <param name="trustSameUser">
    /// Whether a pipe made by the client's own user is the service's, as it is for a portable copy, whose service
    /// is a process of that user. The installed service on Windows runs as SYSTEM, so there it is not.
    /// </param>
    /// <exception cref="UnauthorizedAccessException">The pipe's creator is not trusted.</exception>
    void EnsureTrustedServer(NamedPipeClientStream pipe, string pipeName, bool trustSameUser);

    /// <summary>Whether the client may change the settings of an installed service. False when it cannot be told.</summary>
    bool IsElevatedAdministrator(NamedPipeServerStream pipe);

    /// <summary>Whether the client runs as the same user as this server. False when it cannot be told.</summary>
    bool IsSameUser(NamedPipeServerStream pipe);
}

public static class PipeAccess
{
    /// <summary>The rules of the operating system this process runs on.</summary>
    public static IPipeAccess Current { get; } = OperatingSystem.IsWindows() ? new WindowsPipeAccess() : new UnixPipeAccess();
}
