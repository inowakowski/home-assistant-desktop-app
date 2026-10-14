using System.Diagnostics;
using System.Text;

namespace HADA.Service.CustomSensors;

public sealed record CommandResult(int ExitCode, string Output, string Error);

/// <summary>
/// Runs the command of a custom sensor or button in the system's own shell: Windows PowerShell, which ships with
/// every supported Windows on x64 and ARM64, and <c>sh</c> elsewhere.
/// </summary>
public static class CommandRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    // Full paths, so a shell planted elsewhere on PATH is never run with the service's privileges.
    private static readonly string ExecutablePath = OperatingSystem.IsWindows()
        ? Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe")
        : "/bin/sh";

    // Output as UTF-8 regardless of the console code page; setting it fails harmlessly when there is no console.
    private const string PowerShellPreamble =
        "$ProgressPreference = 'SilentlyContinue'; try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch { }; ";

    /// <summary>What the shell is called in messages: <c>PowerShell</c> or <c>sh</c>.</summary>
    public static string ShellName { get; } = OperatingSystem.IsWindows() ? "PowerShell" : "sh";

    /// <exception cref="TimeoutException">The command ran longer than <paramref name="timeout"/> and was killed.</exception>
    public static async Task<CommandResult> RunAsync(string command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var startInfo = new ProcessStartInfo(ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (OperatingSystem.IsWindows())
        {
            // Encoded, so quotes and other special characters in the command need no escaping.
            foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand" })
            {
                startInfo.ArgumentList.Add(argument);
            }

            startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(PowerShellPreamble + command)));
        }
        else
        {
            // One argument, passed on as it is: the shell parses the command, nothing before it does.
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(command);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"{ShellName} could not be started.");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            // Nothing to feed it; closing input keeps a command that reads from it from waiting forever.
            process.StandardInput.Close();

            var output = process.StandardOutput.ReadToEndAsync(limit.Token);
            var error = process.StandardError.ReadToEndAsync(limit.Token);
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            return new CommandResult(process.ExitCode, (await output.ConfigureAwait(false)).Trim(), (await error.ConfigureAwait(false)).Trim());
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new TimeoutException($"The {ShellName} command did not finish within {timeout.TotalSeconds:0} s.");
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Already gone, or going.
        }
    }
}
