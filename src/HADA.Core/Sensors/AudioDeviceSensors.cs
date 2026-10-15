using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Hosting;
using HADA.Core.Messaging;
using Microsoft.Extensions.Logging;

namespace HADA.Core.Sensors;

/// <summary>
/// Publishes whether the default microphone is muted, with its input level as an attribute. Runs in the user's
/// session, because audio devices belong to it.
/// </summary>
/// <remarks>
/// This is the mute of the device itself: the system's sound settings, or a microphone-mute key. An app that mutes
/// only its own stream, as the mute button of most call apps does, leaves the device unmuted and is not seen here.
/// </remarks>
public sealed class MicrophoneMuteSensor(IEventBus bus, IEntityRegistry registry, IAudioDevices audio) : EagerBackgroundService
{
    public const string EntityId = BuiltInEntityIds.MicrophoneMuted;

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

        var publisher = new ChangeOnlyPublisher(bus);
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            // Without a microphone there is nothing to report; the last state stays until one is connected again.
            if (audio.Read(AudioEndpoint.Microphone) is { } microphone)
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
}

/// <summary>
/// Publishes the name of the default playback device, with the default microphone as an attribute, so an
/// automation can tell headphones from speakers. Runs in the user's session, because audio devices belong to it.
/// </summary>
public sealed partial class AudioDeviceSensor(IEventBus bus, IEntityRegistry registry, IAudioDevices audio, ILogger<AudioDeviceSensor> logger)
    : EagerBackgroundService
{
    public const string EntityId = BuiltInEntityIds.AudioDevice;

    /// <summary>State while there is no playback device. A state cannot be empty.</summary>
    public const string NoDevice = "none";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await registry.RegisterAsync(
            new EntityDescriptor { Id = EntityId, Name = "Audio device", Kind = EntityKind.Sensor, Icon = "mdi:speaker" },
            stoppingToken);

        var publisher = new ChangeOnlyPublisher(bus);
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            string? speakers, microphone;
            try
            {
                speakers = audio.ReadName(AudioEndpoint.Speakers);
                microphone = audio.ReadName(AudioEndpoint.Microphone);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A device that is being switched; the next reading has it.
                LogReadFailed(logger, ex);
                continue;
            }

            await publisher.PublishAsync(
                EntityId,
                speakers is { Length: > 0 } name ? name : NoDevice,
                new Dictionary<string, object?> { ["microphone"] = microphone },
                stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Reading the default audio devices failed.")]
    private static partial void LogReadFailed(ILogger logger, Exception exception);
}
