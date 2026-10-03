using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Hosting;
using HADA.Core.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Windows.Media.Control;

namespace HADA.Tray.Session;

/// <param name="Status"><c>playing</c>, <c>paused</c>, <c>stopped</c> or <c>idle</c> when no app offers media controls.</param>
/// <param name="App">The app the media belongs to, as Windows identifies it, e.g. <c>Spotify.exe</c>.</param>
public sealed record MediaPlayback(string Status, string? Title, string? Artist, string? Album, string? App)
{
    public const string Idle = "idle";

    public static MediaPlayback Nothing { get; } = new(Idle, null, null, null, null);
}

/// <summary>Reads what Windows shows in its own media controls (the flyout above the volume slider).</summary>
public static class SystemMedia
{
    public static async Task<MediaPlayback> ReadAsync()
    {
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        if (manager.GetCurrentSession() is not { } session)
        {
            return MediaPlayback.Nothing;
        }

        var status = session.GetPlaybackInfo()?.PlaybackStatus switch
        {
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => "playing",
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => "paused",
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => "stopped",
            _ => MediaPlayback.Idle,
        };

        var media = await session.TryGetMediaPropertiesAsync();
        return new MediaPlayback(
            status, NullIfEmpty(media?.Title), NullIfEmpty(media?.Artist), NullIfEmpty(media?.AlbumTitle), NullIfEmpty(session.SourceAppUserModelId));
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>
/// Publishes whether media is playing, with title, artist, album and app as attributes. Lives in the tray app:
/// media sessions belong to the signed-in user, and reading them needs the Windows Runtime, which only the tray
/// app is built against.
/// </summary>
public sealed partial class MediaPlaybackSensor(IEventBus bus, IEntityRegistry registry, ILogger<MediaPlaybackSensor> logger)
    : EagerBackgroundService
{
    public const string EntityId = BuiltInEntityIds.MediaPlayback;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Leave the thread that starts the host before the first Windows Runtime call.
        await Task.Yield();

        // Registered after the first successful read: no entity rather than a dead one on a Windows without
        // media sessions (Windows 10 before 1809, some editions).
        var registered = false;
        var failing = false;
        var publisher = new ChangeOnlyPublisher(bus);
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            MediaPlayback playback;
            try
            {
                playback = await SystemMedia.ReadAsync();
                failing = false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Once per stretch of failures: a media app that is just closing, or a Windows that cannot do this.
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
                await registry.RegisterAsync(
                    new EntityDescriptor { Id = EntityId, Name = "Media playback", Kind = EntityKind.Sensor, Icon = "mdi:music-circle" },
                    stoppingToken);
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
