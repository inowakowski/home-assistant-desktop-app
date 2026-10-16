using System.Globalization;
using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Messaging;
using HADA.Core.Models;
using Microsoft.Extensions.Logging;

namespace HADA.Core.Actions;

/// <summary>
/// Lets Home Assistant set the screen's brightness, and reports it, on Linux and macOS. Registers nothing where
/// there is no screen to dim, as on a desktop.
/// </summary>
public sealed partial class ScreenBrightnessControl(IEventBus bus, IEntityRegistry registry, ILogger<ScreenBrightnessControl> logger, IScreenBrightness brightness)
    : CommandHandler(bus, registry, logger), IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly ChangeOnlyPublisher _publisher = new(bus);
    private readonly SemaphoreSlim _reporting = new(1, 1);

    protected override IReadOnlyList<EntityDescriptor> Entities { get; } = brightness.ReadPercent() is null ? [] : [BuiltInDescriptors.ScreenBrightness];

    public override void Dispose()
    {
        _reporting.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    protected override async ValueTask HandleAsync(ActionCommand command, CancellationToken cancellationToken)
    {
        if (!double.TryParse(command.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
        {
            return;
        }

        if (brightness.SetPercent((int)Math.Round(Math.Clamp(percent, 0, 100))) is { } reason)
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
            if (brightness.ReadPercent() is { } percent)
            {
                await _publisher.PublishAsync(
                    BuiltInEntityIds.ScreenBrightness, percent.ToString(CultureInfo.InvariantCulture), cancellationToken: cancellationToken);
            }
        }
        finally
        {
            _reporting.Release();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The screen's brightness could not be set: {Reason}")]
    private static partial void LogFailed(ILogger logger, string reason);
}

/// <summary>"Turn off display" and "Wake display" buttons, on Linux and macOS. Left out where the screens cannot be switched.</summary>
public sealed partial class DisplayPowerActions(IEventBus bus, IEntityRegistry registry, ILogger<DisplayPowerActions> logger, IDisplayPower display)
    : CommandHandler(bus, registry, logger)
{
    protected override IReadOnlyList<EntityDescriptor> Entities { get; } =
        display.CanSwitch ? [BuiltInDescriptors.TurnOffDisplay, BuiltInDescriptors.WakeDisplay] : [];

    protected override ValueTask HandleAsync(ActionCommand command, CancellationToken cancellationToken)
    {
        var reason = command.ActionId == BuiltInEntityIds.TurnOffDisplay ? display.TurnOff() : display.Wake();
        if (reason is not null)
        {
            LogFailed(Logger, command.ActionId, reason);
        }

        return ValueTask.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "'{EntityId}' failed: {Reason}")]
    private static partial void LogFailed(ILogger logger, string entityId, string reason);
}
