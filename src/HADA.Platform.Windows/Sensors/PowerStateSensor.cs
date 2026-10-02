using System.Threading.Channels;
using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Messaging;
using HADA.Platform.Windows.Interop;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HADA.Platform.Windows.Sensors;

/// <summary>
/// Publishes whether the display is on and, on computers with a battery, whether the lid is open.
/// Both come from power notifications, so changes are reported at once instead of on the next poll.
/// </summary>
public sealed partial class PowerStateSensor(IEventBus bus, IEntityRegistry registry, ILogger<PowerStateSensor> logger)
    : BackgroundService
{
    public const string DisplayEntityId = "display_on";
    public const string LidEntityId = "lid_open";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var changes = Channel.CreateUnbounded<(Guid Setting, int Value)>();
        using var listener = new PowerSettingListener((setting, value) => changes.Writer.TryWrite((setting, value)));

        // The current values arrive as soon as a setting is watched; they wait in the channel until the loop below.
        if (listener.TryRegister(PowerSettingListener.ConsoleDisplayState, out var error))
        {
            await registry.RegisterAsync(
                new EntityDescriptor
                {
                    Id = DisplayEntityId,
                    Name = "Display",
                    Kind = EntityKind.BinarySensor,
                    Icon = "mdi:monitor",
                },
                stoppingToken);
        }
        else
        {
            LogUnavailable(logger, DisplayEntityId, error);
        }

        if (SystemPower.TryRead() is { HasBattery: true })
        {
            if (listener.TryRegister(PowerSettingListener.LidSwitchState, out error))
            {
                await registry.RegisterAsync(
                    new EntityDescriptor
                    {
                        Id = LidEntityId,
                        Name = "Lid",
                        Kind = EntityKind.BinarySensor,
                        DeviceClass = "opening",
                        Icon = "mdi:laptop",
                    },
                    stoppingToken);
            }
            else
            {
                LogUnavailable(logger, LidEntityId, error);
            }
        }

        var publisher = new ChangeOnlyPublisher(bus);
        await foreach (var (setting, value) in changes.Reader.ReadAllAsync(stoppingToken))
        {
            if (setting == PowerSettingListener.ConsoleDisplayState)
            {
                var detail = value switch { 0 => "off", 2 => "dimmed", _ => "on" };
                await publisher.PublishAsync(
                    DisplayEntityId,
                    BinaryState.From(value != 0),
                    new Dictionary<string, object?> { ["display_state"] = detail },
                    stoppingToken);
            }
            else if (setting == PowerSettingListener.LidSwitchState)
            {
                await publisher.PublishAsync(LidEntityId, BinaryState.From(value != 0), cancellationToken: stoppingToken);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sensor '{EntityId}' is unavailable: registering for power notifications failed with error {Error}.")]
    private static partial void LogUnavailable(ILogger logger, string entityId, uint error);
}
