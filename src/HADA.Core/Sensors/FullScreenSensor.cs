using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Hosting;
using HADA.Core.Messaging;

namespace HADA.Core.Sensors;

/// <summary>
/// Publishes whether an app fills the screen, a game, a film or a slide show, so that Home Assistant can dim the
/// lights or hold notifications back. What fills it is the attribute <c>kind</c>. Runs in the user's session; left
/// out where the system does not tell.
/// </summary>
public sealed class FullScreenSensor(IEventBus bus, IEntityRegistry registry, IFullScreen fullScreen) : EagerBackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (fullScreen.Read() is null)
        {
            return;
        }

        await registry.RegisterAsync(
            new EntityDescriptor { Id = BuiltInEntityIds.FullScreenApp, Name = "Full-screen app", Kind = EntityKind.BinarySensor, Icon = "mdi:fullscreen" },
            stoppingToken);

        var publisher = new ChangeOnlyPublisher(bus);
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            if (fullScreen.Read() is { } kind)
            {
                await publisher.PublishAsync(
                    BuiltInEntityIds.FullScreenApp,
                    BinaryState.From(kind != FullScreenKind.None),
                    new Dictionary<string, object?> { ["kind"] = kind == FullScreenKind.None ? "none" : kind.ToString().ToLowerInvariant() },
                    stoppingToken);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
