using System.Diagnostics;
using System.Runtime.InteropServices;
using HADA.Core.Abstractions;
using HADA.Platform.Windows.Interop;

namespace HADA.Platform.Windows.Actions;

/// <summary>
/// Sleep goes through <c>SetSuspendState</c>; the rest through Windows' own <c>shutdown.exe</c>, which asks open
/// apps to close and lets them object, as shutting down from the Start menu does. Nothing is forced.
/// </summary>
public sealed class WindowsPowerControl : IPowerControl
{
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint PrivilegeEnabled = 0x0002;
    private const int ErrorNotAllAssigned = 1300;
    private const nint CurrentProcess = -1;

    private static readonly TimeSpan ShutdownToolTimeout = TimeSpan.FromSeconds(10);

    public string? Execute(PowerCommand command) => command switch
    {
        PowerCommand.Sleep => Sleep(),
        PowerCommand.Hibernate => RunShutdownTool("/h"),
        PowerCommand.Shutdown => RunShutdownTool("/s", "/t", "0"),
        PowerCommand.Restart => RunShutdownTool("/r", "/t", "0"),
        _ => $"Unknown power command '{command}'.",
    };

    /// <summary>
    /// Enables the privilege that suspending needs. Every account that may shut the computer down holds it, but
    /// switched off. Returns a Win32 error code, 0 on success.
    /// </summary>
    public static int EnableShutdownPrivilege()
    {
        if (!NativeMethods.OpenProcessToken(CurrentProcess, TokenAdjustPrivileges | TokenQuery, out var token))
        {
            return Marshal.GetLastPInvokeError();
        }

        try
        {
            if (!NativeMethods.LookupPrivilegeValue(null, "SeShutdownPrivilege", out var luid))
            {
                return Marshal.GetLastPInvokeError();
            }

            var privilege = new TokenPrivilege { Count = 1, Luid = luid, Attributes = PrivilegeEnabled };
            if (!NativeMethods.AdjustTokenPrivileges(token, false, privilege, 0, 0, 0))
            {
                return Marshal.GetLastPInvokeError();
            }

            // The call succeeds even when the account does not hold the privilege at all.
            return Marshal.GetLastPInvokeError() == ErrorNotAllAssigned ? ErrorNotAllAssigned : 0;
        }
        finally
        {
            NativeMethods.CloseHandle(token);
        }
    }

    private static string? Sleep()
    {
        var error = EnableShutdownPrivilege();
        if (error != 0)
        {
            return $"the shutdown privilege could not be enabled (error {error}).";
        }

        return NativeMethods.SetSuspendState(hibernate: false, force: false, wakeupEventsDisabled: false)
            ? null
            : $"Windows refused to sleep (error {Marshal.GetLastPInvokeError()}).";
    }

    private static string? RunShutdownTool(params string[] arguments)
    {
        // Full path, so a shutdown.exe planted elsewhere on PATH is never run with the service's privileges.
        var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return "shutdown.exe could not be started.";
            }

            // It returns at once, having asked Windows; a refusal, e.g. hibernation being switched off, is its exit code.
            return !process.WaitForExit(ShutdownToolTimeout) || process.ExitCode == 0
                ? null
                : $"shutdown.exe {string.Join(' ', arguments)} exited with code {process.ExitCode}.";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return ex.Message;
        }
    }
}
