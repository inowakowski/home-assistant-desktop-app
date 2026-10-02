using System.Diagnostics;
using System.IO;
using HADA.Core;
using Microsoft.Extensions.Logging;

namespace HADA.Tray;

/// <summary>
/// Runs the service of a portable copy for as long as its tray app runs. Installed, the service is a Windows
/// service that Windows looks after; here it is an ordinary process without a window, started from the copy's
/// <c>service</c> folder, started again if it dies, and told to stop when the tray app exits.
/// </summary>
internal sealed partial class PortableService : IDisposable
{
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(8);

    private readonly string _executable;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _lock = new();
    private Process? _process;

    public PortableService(string portableRoot, ILogger logger)
    {
        _executable = Path.Combine(portableRoot, "service", "HADA.Service.exe");
        _logger = logger;
    }

    /// <exception cref="FileNotFoundException">The copy has no service to start; it was unpacked only in part.</exception>
    public void Start()
    {
        if (!File.Exists(_executable))
        {
            throw new FileNotFoundException($"This portable copy of HADA is incomplete: {_executable} is missing.", _executable);
        }

        Launch();
    }

    /// <summary>Asks the service to stop, so it can tell Home Assistant it is going, and waits a moment for it.</summary>
    public void Dispose()
    {
        _stopping.Cancel();
        Process? process;
        lock (_lock)
        {
            process = _process;
            _process = null;
        }

        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    if (EventWaitHandle.TryOpenExisting(AppInstance.ServiceStopEventName, out var stop))
                    {
                        using (stop)
                        {
                            stop.Set();
                        }
                    }

                    // It also stops by itself when this process is gone; this only gives it the time to do so tidily.
                    if (!process.WaitForExit(StopTimeout))
                    {
                        process.Kill();
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Gone already.
            }

            process.Dispose();
        }

        _stopping.Dispose();
    }

    private void Launch()
    {
        var start = new ProcessStartInfo(_executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(_executable)!,
        };

        // So that the service ends when this process does, however that happens.
        start.ArgumentList.Add("--parent");
        start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var process = Process.Start(start) ?? throw new InvalidOperationException("The HADA service could not be started.");
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => OnExited(process);
        lock (_lock)
        {
            _process = process;
        }

        LogStarted(_logger, process.Id);
    }

    private void OnExited(Process process)
    {
        if (_stopping.IsCancellationRequested)
        {
            return;
        }

        LogExited(_logger, process.ExitCode, RestartDelay);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(RestartDelay, _stopping.Token);
                Launch();
            }
            catch (OperationCanceledException)
            {
                // The tray app is exiting.
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ObjectDisposedException)
            {
                LogRestartFailed(_logger, ex.Message);
            }
        });
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Started this portable copy's service (process {ProcessId}).")]
    private static partial void LogStarted(ILogger logger, int processId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The service ended by itself with exit code {ExitCode}; starting it again in {Delay}.")]
    private static partial void LogExited(ILogger logger, int exitCode, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Error, Message = "The service could not be started again: {Reason}")]
    private static partial void LogRestartFailed(ILogger logger, string reason);
}
