using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Messaging;
using HADA.Core.Models;
using Microsoft.Extensions.Logging;

namespace HADA.Core.Actions;

/// <summary>
/// A switch to turn "Do not disturb" on and off, reporting how it is. Runs in the user's session, where the setting
/// belongs. Left out where the system does not let it be switched, as macOS does not.
/// </summary>
public sealed partial class DoNotDisturbControl(IEventBus bus, IEntityRegistry registry, ILogger<DoNotDisturbControl> logger, IDoNotDisturb quiet)
    : CommandHandler(bus, registry, logger), IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly ChangeOnlyPublisher _publisher = new(bus);
    private readonly SemaphoreSlim _reporting = new(1, 1);

    protected override IReadOnlyList<EntityDescriptor> Entities { get; } = quiet.CanSet ? [BuiltInDescriptors.SilenceNotifications] : [];

    public override void Dispose()
    {
        _reporting.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    protected override async ValueTask HandleAsync(ActionCommand command, CancellationToken cancellationToken)
    {
        if (quiet.Set(command.Value == BinaryState.On) is { } reason)
        {
            LogFailed(Logger, reason);
        }

        // Home Assistant shows the new state at once, instead of the old one until the next poll.
        await ReportAsync(cancellationToken);
    }

    protected override async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Entities.Count == 0)
        {
            return;
        }

        try
        {
            using var timer = new PeriodicTimer(PollInterval);
            do
            {
                await ReportAsync(cancellationToken);
            }
            while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task ReportAsync(CancellationToken cancellationToken)
    {
        await _reporting.WaitAsync(cancellationToken);
        try
        {
            if (quiet.Read() is { } on)
            {
                await _publisher.PublishAsync(BuiltInEntityIds.SilenceNotifications, BinaryState.From(on), cancellationToken: cancellationToken);
            }
        }
        finally
        {
            _reporting.Release();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Do not disturb could not be switched: {Reason}")]
    private static partial void LogFailed(ILogger logger, string reason);
}
