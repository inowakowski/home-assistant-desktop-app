using System.Runtime.InteropServices;
using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HADA.Platform.Windows.Sensors;

/// <summary>
/// Publishes whether the default microphone is muted in Windows, with its input level as an attribute.
/// Runs in the tray app, because audio devices belong to the user's session.
/// </summary>
/// <remarks>
/// This is the mute of the device itself: the Sound settings, or a microphone-mute key. An app that mutes only
/// its own stream, as the mute button of most call apps does, leaves the device unmuted and is not seen here.
/// </remarks>
public sealed partial class MicrophoneMuteSensor(IEventBus bus, IEntityRegistry registry, ILogger<MicrophoneMuteSensor> logger)
    : BackgroundService
{
    public const string EntityId = "microphone_muted";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await registry.RegisterAsync(
            new EntityDescriptor
            {
                Id = EntityId,
                Name = "Microphone muted",
                Kind = EntityKind.BinarySensor,
                Icon = "mdi:microphone-off",
            },
            stoppingToken);

        using var endpoint = new DefaultAudioEndpoint(AudioDevice.Microphone);
        var publisher = new ChangeOnlyPublisher(bus);
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

            // Without a microphone there is nothing to report; the last state stays until one is connected again.
            if (current is { } microphone)
            {
                await publisher.PublishAsync(
                    EntityId,
                    BinaryState.From(microphone.IsMuted),
                    new Dictionary<string, object?> { ["level"] = microphone.VolumePercent },
                    stoppingToken);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Reading the microphone's mute state failed.")]
    private static partial void LogReadFailed(ILogger logger, Exception exception);
}
