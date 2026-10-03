using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace HADA.Ipc;

/// <summary>Which session of the operating system a tray runs in, and which session somebody is using right now.</summary>
public interface ISessionDirectory
{
    /// <summary>The session shown on the computer's own screen, or <see langword="null"/> while Windows switches users.</summary>
    uint? ConsoleSessionId { get; }

    /// <summary>The session of the client at the other end of <paramref name="pipe"/>, or <see langword="null"/> when it cannot be told.</summary>
    uint? GetClientSessionId(NamedPipeServerStream pipe, string clientName);

    /// <summary>
    /// Whether somebody is connected to the session: at the computer itself or over Remote Desktop. False for the
    /// session of a user who is signed in but was switched away from.
    /// </summary>
    bool IsConnected(uint sessionId);
}

public static class SessionDirectory
{
    /// <summary>The sessions of the operating system this process runs on.</summary>
    /// <exception cref="PlatformNotSupportedException">HADA cannot tell sessions apart on this operating system yet.</exception>
    public static ISessionDirectory Current { get; } = OperatingSystem.IsWindows()
        ? new WindowsSessionDirectory()
        : throw new PlatformNotSupportedException("HADA cannot tell user sessions apart on this operating system yet.");
}

[SupportedOSPlatform("windows")]
public sealed class WindowsSessionDirectory : ISessionDirectory
{
    private const uint NoConsoleSession = 0xFFFFFFFF;
    private const int ConnectStateClass = 8;
    private const int StateActive = 0;

    public uint? ConsoleSessionId => WTSGetActiveConsoleSessionId() is var id && id != NoConsoleSession ? id : null;

    public uint? GetClientSessionId(NamedPipeServerStream pipe, string clientName)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        return GetNamedPipeClientSessionId(pipe.SafePipeHandle, out var sessionId) ? sessionId : null;
    }

    public bool IsConnected(uint sessionId)
    {
        if (!WTSQuerySessionInformation(0, sessionId, ConnectStateClass, out var buffer, out var size) || buffer == 0)
        {
            return false;
        }

        try
        {
            return size >= sizeof(int) && Marshal.ReadInt32(buffer) == StateActive;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint sessionId);

    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(nint server, uint sessionId, int infoClass, out nint buffer, out uint bytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(nint memory);
}
