using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Messaging;
using Microsoft.Extensions.Hosting;

namespace HADA.Platform.Windows.Sensors;

/// <summary>
/// Publishes whether someone used the keyboard or mouse in the last minute. Must run in the user's session (the tray app).
/// </summary>
/// <remarks>
/// A short, fixed window keeps the sensor responsive; "idle for 10 minutes" is better expressed in Home Assistant
/// with a <c>for:</c> condition on the off state.
/// </remarks>
public sealed class UserActivitySensor(IEventBus bus, IEntityRegistry registry) : BackgroundService
{
    public const string EntityId = "user_active";

    public static readonly TimeSpan ActiveWindow = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await registry.RegisterAsync(
            new EntityDescriptor
            {
                Id = EntityId,
                Name = "User active",
                Kind = EntityKind.BinarySensor,
                Icon = "mdi:account-clock",
            },
            stoppingToken);

        var publisher = new ChangeOnlyPublisher(bus);
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            if (UserInput.TryReadIdleTime() is { } idle)
            {
                await publisher.PublishAsync(EntityId, BinaryState.From(idle < ActiveWindow), cancellationToken: stoppingToken);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
