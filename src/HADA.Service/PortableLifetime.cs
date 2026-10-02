using System.Diagnostics;
using HADA.Core;

namespace HADA.Service;

/// <summary>
/// Ends the service of a portable copy when its tray app says so, or goes away. Installed, Windows starts and stops
/// the service; a portable copy's service is a process the tray app started, and it must not outlive it.
/// </summary>
public sealed partial class PortableLifetime(
    IHostApplicationLifetime lifetime, IConfiguration configuration, ILogger<PortableLifetime> logger) : BackgroundService
{
    /// <summary>The tray app's process id, passed as <c>--parent 1234</c>.</summary>
    public const string ParentArgument = "parent";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var stop = new EventWaitHandle(initialState: false, EventResetMode.ManualReset, AppInstance.ServiceStopEventName);
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ThreadPool.RegisterWaitForSingleObject(
            stop, (_, _) => asked.TrySetResult(), state: null, Timeout.Infinite, executeOnlyOnce: true);
        try
        {
            var reason = await Task.WhenAny(asked.Task, ParentExitedAsync(stoppingToken)) == asked.Task
                ? "the tray app asked for it"
                : "the tray app is gone";
            if (!stoppingToken.IsCancellationRequested)
            {
                LogStopping(logger, reason);
                lifetime.StopApplication();
            }
        }
        finally
        {
            registration.Unregister(null);
        }
    }

    /// <summary>Completes when the tray app's process exits; never, when no process was named or it cannot be watched.</summary>
    private async Task ParentExitedAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (int.TryParse(configuration[ParentArgument], out var parentId))
            {
                using var parent = Process.GetProcessById(parentId);
                await parent.WaitForExitAsync(cancellationToken);
                return;
            }
        }
        catch (ArgumentException)
        {
            // Already gone before this could look.
            return;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Cannot be watched; the stop event still works.
        }

        await Task.Delay(Timeout.Infinite, cancellationToken).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Stopping: {Reason}.")]
    private static partial void LogStopping(ILogger logger, string reason);
}
