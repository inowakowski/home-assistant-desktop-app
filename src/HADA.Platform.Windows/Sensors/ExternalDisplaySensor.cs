using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Messaging;
using HADA.Platform.Windows.Interop;
using Microsoft.Extensions.Hosting;

namespace HADA.Platform.Windows.Sensors;

/// <param name="Total">Monitors connected, the built-in panel included.</param>
/// <param name="External">Monitors connected through a cable, dock or wireless display.</param>
public readonly record struct ConnectedDisplays(int Total, int External);

public static class Displays
{
    private const uint AllPaths = 1;
    private const int InsufficientBuffer = 122;
    private const int ModeInfoSize = 64;

    // DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY values of panels built into the computer.
    private const uint Lvds = 6;
    private const uint EmbeddedDisplayPort = 11;
    private const uint EmbeddedUdi = 13;
    private const uint Internal = 0x80000000;

    /// <summary>
    /// Counts the monitors Windows sees, whether or not they are in use. Must run in the user's session.
    /// A monitor in standby still counts; one without power does not, because its connector reports nothing.
    /// Returns <see langword="null"/> when the display configuration cannot be read.
    /// </summary>
    public static unsafe ConnectedDisplays? TryRead()
    {
        // The configuration can change between asking for the sizes and fetching it, so allow a few attempts.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (NativeMethods.GetDisplayConfigBufferSizes(AllPaths, out var pathCount, out var modeCount) != 0)
            {
                return null;
            }

            var paths = new DisplayConfigPath[pathCount];
            var modes = new byte[Math.Max(modeCount, 1) * ModeInfoSize];
            int result;
            fixed (DisplayConfigPath* pathBuffer = paths)
            fixed (byte* modeBuffer = modes)
            {
                result = NativeMethods.QueryDisplayConfig(AllPaths, ref pathCount, pathBuffer, ref modeCount, modeBuffer, 0);
            }

            if (result == InsufficientBuffer)
            {
                continue;
            }

            if (result != 0)
            {
                return null;
            }

            // Every output is listed once per source it could be driven from; count each output once.
            var outputs = new Dictionary<(long Adapter, uint Target), uint>();
            for (var i = 0; i < pathCount; i++)
            {
                if (paths[i].TargetAvailable != 0)
                {
                    outputs[(paths[i].TargetAdapterId, paths[i].TargetId)] = paths[i].OutputTechnology;
                }
            }

            var external = outputs.Values.Count(technology =>
                technology is not (Internal or Lvds or EmbeddedDisplayPort or EmbeddedUdi));
            return new ConnectedDisplays(outputs.Count, external);
        }

        return null;
    }
}

/// <summary>
/// Publishes whether a monitor other than the built-in one is connected. Runs in the tray app, because the display
/// configuration belongs to the user's session.
/// </summary>
public sealed class ExternalDisplaySensor(IEventBus bus, IEntityRegistry registry) : BackgroundService
{
    public const string EntityId = "external_display";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await registry.RegisterAsync(
            new EntityDescriptor
            {
                Id = EntityId,
                Name = "External display",
                Kind = EntityKind.BinarySensor,
                DeviceClass = "connectivity",
                Icon = "mdi:monitor-multiple",
            },
            stoppingToken);

        var publisher = new ChangeOnlyPublisher(bus);
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            if (Displays.TryRead() is { } displays)
            {
                await publisher.PublishAsync(
                    EntityId,
                    BinaryState.From(displays.External > 0),
                    new Dictionary<string, object?> { ["displays"] = displays.Total, ["external_displays"] = displays.External },
                    stoppingToken);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
