using System.Runtime.InteropServices;
using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HADA.Platform.Windows.Sensors;

/// <summary>
/// Publishes the name of the default playback device, with the default microphone as an attribute, so an
/// automation can tell headphones from speakers. Runs in the tray app, because audio devices belong to the
/// user's session.
/// </summary>
public sealed partial class AudioDeviceSensor(IEventBus bus, IEntityRegistry registry, ILogger<AudioDeviceSensor> logger)
    : BackgroundService
{
    public const string EntityId = "audio_device";

    /// <summary>State while there is no playback device. A state cannot be empty.</summary>
    public const string NoDevice = "none";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await registry.RegisterAsync(
            new EntityDescriptor { Id = EntityId, Name = "Audio device", Kind = EntityKind.Sensor, Icon = "mdi:speaker" },
            stoppingToken);

        using var speakers = new DefaultAudioEndpoint();
        using var microphone = new DefaultAudioEndpoint(AudioDevice.Microphone);
        var publisher = new ChangeOnlyPublisher(bus);
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try
            {
                await publisher.PublishAsync(
                    EntityId,
                    speakers.TryReadName() is { Length: > 0 } name ? name : NoDevice,
                    new Dictionary<string, object?> { ["microphone"] = microphone.TryReadName() },
                    stoppingToken);
            }
            catch (COMException ex)
            {
                LogReadFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Reading the default audio devices failed.")]
    private static partial void LogReadFailed(ILogger logger, Exception exception);
}
