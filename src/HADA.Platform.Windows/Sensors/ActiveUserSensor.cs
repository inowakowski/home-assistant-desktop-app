using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Messaging;
using Microsoft.Extensions.Hosting;

namespace HADA.Platform.Windows.Sensors;

/// <summary>
/// Publishes who is signed in at the computer's own screen. Runs in the service, so it also reports that nobody is.
/// With several users signed in, this is the one whose desktop is shown, and whose tray sensors are reported.
/// </summary>
public sealed class ActiveUserSensor(IEventBus bus, IEntityRegistry registry) : BackgroundService
{
    public const string EntityId = "active_user";

    /// <summary>State while the sign-in screen is shown. A state cannot be empty.</summary>
    public const string Nobody = "none";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await registry.RegisterAsync(
            new EntityDescriptor { Id = EntityId, Name = "Signed-in user", Kind = EntityKind.Sensor, Icon = "mdi:account" },
            stoppingToken);

        var publisher = new ChangeOnlyPublisher(bus);
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            // No console session at all only happens briefly, while Windows switches users.
            if (ConsoleSession.TryReadUserName() is { } user)
            {
                await publisher.PublishAsync(EntityId, user.Length > 0 ? user : Nobody, cancellationToken: stoppingToken);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
