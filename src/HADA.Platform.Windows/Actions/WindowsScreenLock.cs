using System.Diagnostics;
using System.Runtime.InteropServices;
using HADA.Core.Abstractions;
using HADA.Platform.Windows.Interop;

namespace HADA.Platform.Windows.Actions;

/// <remarks>
/// <c>LockWorkStation</c> only works from inside the interactive session. A Windows service runs in session 0,
/// so there the active console session is disconnected instead, which returns it to the lock screen.
/// </remarks>
public sealed class WindowsScreenLock : IScreenLock
{
    public string? Lock()
    {
        using var process = Process.GetCurrentProcess();

        bool locked;
        if (process.SessionId != 0)
        {
            locked = NativeMethods.LockWorkStation();
        }
        else
        {
            var session = NativeMethods.WTSGetActiveConsoleSessionId();
            locked = session != NativeMethods.NoActiveConsoleSession
                && NativeMethods.WTSDisconnectSession(server: 0, session, wait: false);
        }

        return locked ? null : $"error {Marshal.GetLastPInvokeError()}";
    }
}
