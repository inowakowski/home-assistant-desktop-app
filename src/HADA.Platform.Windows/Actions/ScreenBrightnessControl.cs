using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Messaging;
using HADA.Core.Models;
using Microsoft.Extensions.Logging;

namespace HADA.Platform.Windows.Actions;

/// <summary>
/// The brightness of a built-in screen, as the slider in Windows' quick settings shows it: read and set through
/// the monitor's WMI classes, which is the one way that works on every laptop Windows itself can dim.
/// </summary>
public static class ScreenBrightness
{
    private const string Namespace = @"root\WMI";

    /// <summary>The brightness from 0 to 100; <see langword="null"/> where there is no screen Windows can dim, as on a desktop.</summary>
    public static int? TryRead()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(Namespace, "SELECT CurrentBrightness FROM WmiMonitorBrightness WHERE Active = TRUE");
            using var screens = searcher.Get();
            foreach (var screen in screens)
            {
                using (screen)
                {
                    return Math.Clamp(Convert.ToInt32(screen["CurrentBrightness"], CultureInfo.InvariantCulture), 0, 100);
                }
            }
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            // No such class on this computer, or WMI is not there to ask.
        }

        return null;
    }

    public static bool TrySet(int percent)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(Namespace, "SELECT * FROM WmiMonitorBrightnessMethods WHERE Active = TRUE");
            using var screens = searcher.Get();
            foreach (ManagementObject screen in screens)
            {
                using (screen)
                {
                    // The first argument is how long the change may take, in seconds.
                    screen.InvokeMethod("WmiSetBrightness", [1u, (byte)Math.Clamp(percent, 0, 100)]);
                    return true;
                }
            }
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            // Reported by the caller.
        }

        return false;
    }

    private static bool IsExpected(Exception ex) =>
        ex is ManagementException or COMException or UnauthorizedAccessException or PlatformNotSupportedException
            or InvalidCastException or FormatException or OverflowException;
}

/// <summary>
/// Exposes the brightness of the built-in screen as a number that can be set. Registers nothing on a computer
/// without such a screen, so desktops do not get a slider that does nothing.
/// </summary>
public sealed partial class ScreenBrightnessControl(IEventBus bus, IEntityRegistry registry, ILogger<ScreenBrightnessControl> logger)
    : CommandHandler(bus, registry, logger), IDisposable
{
    public const string EntityId = BuiltInEntityIds.ScreenBrightness;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly ChangeOnlyPublisher _publisher = new(bus);

    // The publisher is not thread-safe, and the state is reported both on a timer and right after a command.
    private readonly SemaphoreSlim _reporting = new(1, 1);

    protected override IReadOnlyList<EntityDescriptor> Entities { get; } = ScreenBrightness.TryRead() is null
        ? []
        :
        [
            new()
            {
                Id = EntityId,
                Name = "Screen brightness",
                Kind = EntityKind.Number,
                Icon = "mdi:brightness-6",
                UnitOfMeasurement = "%",
                Min = 0,
                Max = 100,
                Step = 1,
            },
        ];

    public override void Dispose()
    {
        _reporting.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    protected override async ValueTask HandleAsync(ActionCommand command, CancellationToken cancellationToken)
    {
        if (!double.TryParse(command.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)
            || !ScreenBrightness.TrySet((int)Math.Round(percent)))
        {
            LogFailed(Logger, command.Value);
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
            // With the lid closed and the screen off there may be nothing to read; the last value stays.
            if (ScreenBrightness.TryRead() is { } percent)
            {
                await _publisher.PublishAsync(EntityId, percent.ToString(CultureInfo.InvariantCulture), cancellationToken: cancellationToken);
            }
        }
        finally
        {
            _reporting.Release();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The screen's brightness could not be set to '{Value}'.")]
    private static partial void LogFailed(ILogger logger, string? value);
}
