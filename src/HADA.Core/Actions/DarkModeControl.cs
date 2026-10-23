using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Messaging;
using HADA.Core.Models;
using Microsoft.Extensions.Logging;

namespace HADA.Core.Actions;

/// <summary>
/// A switch for the desktop's dark look, reporting how it is, also when the user changes it. Runs in the user's
/// session, where the setting belongs. Left out where the desktop does not let it be switched.
/// </summary>
public sealed partial class DarkModeControl(IEventBus bus, IEntityRegistry registry, ILogger<DarkModeControl> logger, IDarkMode darkMode)
    : CommandHandler(bus, registry, logger), IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly ChangeOnlyPublisher _publisher = new(bus);
    private readonly SemaphoreSlim _reporting = new(1, 1);

    protected override IReadOnlyList<EntityDescriptor> Entities { get; } = darkMode.CanSet
        ? [new() { Id = BuiltInEntityIds.DarkMode, Name = "Dark mode", Kind = EntityKind.Switch, Icon = "mdi:theme-light-dark" }]
        : [];

    public override void Dispose()
    {
        _reporting.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    protected override async ValueTask HandleAsync(ActionCommand command, CancellationToken cancellationToken)
    {
        if (darkMode.Set(command.Value == BinaryState.On) is { } reason)
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
            if (darkMode.Read() is { } dark)
            {
                await _publisher.PublishAsync(BuiltInEntityIds.DarkMode, BinaryState.From(dark), cancellationToken: cancellationToken);
            }
        }
        finally
        {
            _reporting.Release();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dark mode could not be switched: {Reason}")]
    private static partial void LogFailed(ILogger logger, string reason);
}
