using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Messaging;
using Microsoft.Extensions.Hosting;

namespace HADA.Platform.Windows.Sensors;

/// <summary>
/// Publishes whether someone used the keyboard or mouse lately: within the last minute, unless the user chose
/// another threshold. Must run in the user's session (the tray app).
/// </summary>
/// <remarks>
/// A short window keeps the sensor responsive; "idle for 10 minutes" can also be expressed in Home Assistant
/// with a <c>for:</c> condition on the off state.
/// </remarks>
/// <param name="activeWindow">
/// How long after the last input the user still counts as active. Asked on every reading, so a changed setting
/// takes effect at once.
/// </param>
public sealed class UserActivitySensor(IEventBus bus, IEntityRegistry registry, Func<TimeSpan>? activeWindow = null) : BackgroundService
{
    public const string EntityId = "user_active";

    public static readonly TimeSpan DefaultActiveWindow = TimeSpan.FromSeconds(60);

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
                await publisher.PublishAsync(
                    EntityId, BinaryState.From(idle < (activeWindow?.Invoke() ?? DefaultActiveWindow)), cancellationToken: stoppingToken);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
