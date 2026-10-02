using System.Diagnostics;
using System.Text;

namespace HADA.Service.CustomSensors;

public sealed record PowerShellResult(int ExitCode, string Output, string Error);

/// <summary>Runs a command in Windows PowerShell, which ships with every supported Windows on x64 and ARM64.</summary>
public static class PowerShellRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    // Full path, so a powershell.exe planted elsewhere on PATH is never run with the service's privileges.
    private static readonly string ExecutablePath =
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    // Output as UTF-8 regardless of the console code page; setting it fails harmlessly when there is no console.
    private const string Preamble =
        "$ProgressPreference = 'SilentlyContinue'; try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch { }; ";

    /// <exception cref="TimeoutException">The command ran longer than <paramref name="timeout"/> and was killed.</exception>
    public static async Task<PowerShellResult> RunAsync(string command, TimeSpan timeout, CancellationToken cancellationToken)
    {
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

        // Encoded, so quotes and other special characters in the command need no escaping.
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand" })
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(Preamble + command)));

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("PowerShell could not be started.");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            // Nothing to feed it; closing input keeps a command that reads from it from waiting forever.
            process.StandardInput.Close();

            var output = process.StandardOutput.ReadToEndAsync(limit.Token);
            var error = process.StandardError.ReadToEndAsync(limit.Token);
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            return new PowerShellResult(process.ExitCode, (await output.ConfigureAwait(false)).Trim(), (await error.ConfigureAwait(false)).Trim());
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new TimeoutException($"The PowerShell command did not finish within {timeout.TotalSeconds:0} s.");
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
