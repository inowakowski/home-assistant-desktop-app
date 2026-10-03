using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Hosting;
using HADA.Core.Messaging;
using Microsoft.Extensions.Hosting;

namespace HADA.Platform.Windows.Sensors;

/// <summary>
/// Publishes whether the session on the physical console is locked. Runs in the service, so it keeps working
/// while nobody is signed in and when the tray app is not running.
/// </summary>
public sealed class SessionLockSensor(IEventBus bus, IEntityRegistry registry) : EagerBackgroundService
{
    public const string EntityId = BuiltInEntityIds.SessionLocked;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await registry.RegisterAsync(
            new EntityDescriptor
            {
                Id = EntityId,
                Name = "Session locked",
                Kind = EntityKind.BinarySensor,
                Icon = "mdi:account-lock",
            },
            stoppingToken);

        var publisher = new ChangeOnlyPublisher(bus);
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            // No console session at all only happens briefly, while Windows switches users.
            if (ConsoleSession.TryRead() is { } session)
            {
                await publisher.PublishAsync(EntityId, BinaryState.From(session.IsLocked), cancellationToken: stoppingToken);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
