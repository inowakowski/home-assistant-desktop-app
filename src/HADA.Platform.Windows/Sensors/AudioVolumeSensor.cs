using System.Globalization;
using System.Runtime.InteropServices;
using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Hosting;
using HADA.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HADA.Platform.Windows.Sensors;

/// <summary>Publishes the default playback device's volume, with mute state as an attribute. Runs in the tray app.</summary>
public sealed partial class AudioVolumeSensor(IEventBus bus, IEntityRegistry registry, ILogger<AudioVolumeSensor> logger)
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

        using var endpoint = new DefaultAudioEndpoint();
        AudioVolumeInfo? last = null;
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            AudioVolumeInfo? current;
            try
            {
                current = endpoint.TryRead();
            }
            catch (COMException ex)
            {
                LogReadFailed(logger, ex);
                continue;
            }

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

    [LoggerMessage(Level = LogLevel.Debug, Message = "Reading the audio endpoint volume failed.")]
    private static partial void LogReadFailed(ILogger logger, Exception exception);
}
