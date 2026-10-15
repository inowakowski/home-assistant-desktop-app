using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Hosting;
using HADA.Core.Messaging;
using Microsoft.Extensions.Logging;

namespace HADA.Core.Sensors;

/// <summary>
/// Publishes whether media is playing, with title, artist, album and app as attributes. Runs in the user's
/// session: media players belong to the signed-in user.
/// </summary>
public sealed partial class MediaPlaybackSensor(IEventBus bus, IEntityRegistry registry, IMediaSessions media, ILogger<MediaPlaybackSensor> logger)
    : EagerBackgroundService
{
    public const string EntityId = BuiltInEntityIds.MediaPlayback;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Leave the thread that starts the host before the first read: Windows Runtime calls must not run on it.
        await Task.Yield();

        // Registered after the first successful read: no entity rather than a dead one where the system has no
        // media controls to ask (Windows 10 before 1809, a Linux without a session bus).
        var registered = false;
        var failing = false;
        var publisher = new ChangeOnlyPublisher(bus);
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            MediaPlayback playback;
            try
            {
                playback = await media.ReadAsync(stoppingToken);
                failing = false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Once per stretch of failures: a media app that is just closing, or a system that cannot do this.
                if (!failing)
                {
                    LogReadFailed(logger, ex.Message);
                    failing = true;
                }

                continue;
            }

            if (!registered)
            {
                registered = true;
                await registry.RegisterAsync(BuiltInDescriptors.MediaPlayback, stoppingToken);
            }

            await publisher.PublishAsync(
                EntityId,
                playback.Status,
                new Dictionary<string, object?>
                {
                    ["title"] = playback.Title,
                    ["artist"] = playback.Artist,
                    ["album"] = playback.Album,
                    ["app"] = playback.App,
                },
                stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Media playback cannot be read right now: {Reason}")]
    private static partial void LogReadFailed(ILogger logger, string reason);
}
