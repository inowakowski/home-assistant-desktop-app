using Microsoft.Extensions.Hosting;

namespace HADA.Core.Hosting;

/// <summary>
/// A background service whose <see cref="BackgroundService.ExecuteAsync"/> runs up to its first incomplete await
/// before <see cref="StartAsync"/> returns. Sensors and actions subscribe to the event bus and register their
/// entities there, so whatever the host starts next finds them in place.
/// </summary>
/// <remarks>
/// This is what <see cref="BackgroundService"/> itself did up to .NET 9; from .NET 10 it runs all of
/// <c>ExecuteAsync</c> on a background thread.
/// </remarks>
public abstract class EagerBackgroundService : BackgroundService
{
    private Task? _executeTask;
    private CancellationTokenSource? _stopping;

    /// <summary>What the host watches to log a service that failed.</summary>
    public override Task? ExecuteTask => _executeTask;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _executeTask = ExecuteAsync(_stopping.Token);

        // Already finished, e.g. a sensor for hardware this computer lacks: let a failure surface right here.
        return _executeTask.IsCompleted ? _executeTask : Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_executeTask is null)
        {
            return;
        }

        try
        {
            await _stopping!.CancelAsync().ConfigureAwait(false);
        }
        finally
        {
            // Wait for the service to wind down, but no longer than the host allows; its failure was logged already.
            await _executeTask.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    public override void Dispose()
    {
        _stopping?.Cancel();
        base.Dispose();
        GC.SuppressFinalize(this);
    }
}
