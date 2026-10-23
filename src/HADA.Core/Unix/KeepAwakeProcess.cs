using System.ComponentModel;
using System.Diagnostics;
using HADA.Core.Abstractions;

namespace HADA.Core.Unix;

/// <summary>
/// Holds sleep off with a tool that does so for as long as it runs: <c>systemd-inhibit</c> on Linux, <c>caffeinate</c>
/// on macOS. The tool is told to end when this process does, so a HADA that stops without letting go does not keep
/// the computer awake.
/// </summary>
/// <param name="tool">The tool's name, found where <see cref="Tools.Find"/> looks.</param>
/// <param name="arguments">Its arguments, given the id of this process.</param>
public sealed class KeepAwakeProcess(string tool, Func<int, IReadOnlyList<string>> arguments) : IKeepAwake, IDisposable
{
    // A tool that refuses ends at once; one that holds sleep off goes on running.
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(500);

    private readonly Lock _lock = new();
    private Process? _process;

    public string? Hold()
    {
        lock (_lock)
        {
            if (_process is { HasExited: false })
            {
                return null;
            }

            _process?.Dispose();
            _process = null;
            if (Tools.Find(tool) is not { } path)
            {
                return $"{tool} is missing.";
            }

            var start = new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in arguments(Environment.ProcessId))
            {
                start.ArgumentList.Add(argument);
            }

            start.Environment["LC_ALL"] = "C";
            try
            {
                var process = Process.Start(start);
                if (process is null)
                {
                    return $"{tool} could not be started.";
                }

                if (process.WaitForExit(Settle))
                {
                    var error = process.StandardError.ReadToEnd().Trim();
                    var code = process.ExitCode;
                    process.Dispose();
                    return error.Length > 0 ? $"{tool} exited with code {code}: {error}" : $"{tool} exited with code {code}.";
                }

                _process = process;
                return null;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                return $"{tool} could not be started: {ex.Message}";
            }
        }
    }

    public void Release()
    {
        lock (_lock)
        {
            if (_process is not { } process)
            {
                return;
            }

            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // It ended meanwhile.
            }

            process.Dispose();
            _process = null;
        }
    }

    public void Dispose() => Release();
}
