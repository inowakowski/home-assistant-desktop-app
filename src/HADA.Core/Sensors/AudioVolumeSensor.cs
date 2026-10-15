using System.Globalization;
using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Hosting;
using HADA.Core.Models;

namespace HADA.Core.Sensors;

/// <summary>Publishes the default playback device's volume, with mute state as an attribute. Runs in the user's session.</summary>
public sealed class AudioVolumeSensor(IEventBus bus, IEntityRegistry registry, IAudioDevices audio)
    : EagerBackgroundService
{
    public const string EntityId = BuiltInEntityIds.AudioVolume;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await registry.RegisterAsync(
            new EntityDescriptor
            {
                Id = EntityId,
                Name = "Volume",
                Kind = EntityKind.Sensor,
                Icon = "mdi:volume-high",
                UnitOfMeasurement = "%",
            },
            stoppingToken);

        AudioLevel? last = null;
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            var current = audio.Read(AudioEndpoint.Speakers);
            if (current is not { } volume || current == last)
            {
                continue;
            }

            last = current;
            await bus.PublishAsync(
                new TelemetryEvent
                {
                    SensorId = EntityId,
                    State = volume.VolumePercent.ToString(CultureInfo.InvariantCulture),
                    Attributes = new Dictionary<string, object?> { ["muted"] = volume.IsMuted },
                },
                stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
