using System.Globalization;
using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Messaging;
using HADA.Core.Models;
using HADA.Platform.Windows.Actions;
using HADA.Platform.Windows.Interop;
using HADA.Platform.Windows.Sensors;
using Microsoft.Extensions.Logging;
using Windows.Media.Control;

namespace HADA.Tray.Session;

/// <summary>
/// The computer as a media player: what is playing, how loud, and play, pause, next, previous, stop, volume and
/// mute in one entity, the way Home Assistant shows a television or a speaker. Only the HADA integration can show
/// such an entity; the separate buttons, the volume slider and the playback sensor stay for the other ways.
/// </summary>
/// <remarks>
/// Runs in the tray app, as media sessions and the sound belong to the signed-in user. Commands go to the media
/// session Windows shows in its own controls, which tells play from pause; the multimedia keys, which can only
/// toggle, are the way out when there is no session to tell.
/// </remarks>
public sealed partial class MediaPlayerControl(IEventBus bus, IEntityRegistry registry, ILogger<MediaPlayerControl> logger)
    : CommandHandler(bus, registry, logger), IDisposable
{
    public const string EntityId = BuiltInEntityIds.MediaPlayer;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly DefaultAudioEndpoint _speakers = new();
    private readonly ChangeOnlyPublisher _publisher = new(bus);

    // The publisher is not thread-safe, and the state is reported both on a timer and right after a command.
    private readonly SemaphoreSlim _reporting = new(1, 1);

    protected override IReadOnlyList<EntityDescriptor> Entities { get; } =
    [
        new() { Id = EntityId, Name = "Media player", Kind = EntityKind.MediaPlayer, Icon = "mdi:play-box-outline" },
    ];

    public override void Dispose()
    {
        _speakers.Dispose();
        _reporting.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    protected override async ValueTask HandleAsync(ActionCommand command, CancellationToken cancellationToken)
    {
        var argument = command.GetParameter(MediaCommand.Value);
        var done = command.Value switch
        {
            MediaCommand.SetVolume => int.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out var percent)
                && _speakers.TrySetVolume(Math.Clamp(percent, 0, 100)),
            MediaCommand.Mute => _speakers.TrySetMute(argument == BinaryState.On),
            { } action => await ControlPlaybackAsync(action),
            _ => false,
        };

        if (!done)
        {
            LogFailed(Logger, command.Value);
        }

        // Home Assistant shows the new state at once, instead of the old one until the next poll.
        await ReportAsync(cancellationToken);
    }

    protected override async Task RunAsync(CancellationToken cancellationToken)
    {
        // Leave the thread that starts the host before the first Windows Runtime call.
        await Task.Yield();
        try
        {
            using var timer = new PeriodicTimer(PollInterval);
            do
            {
                await ReportAsync(cancellationToken);
            }
            while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static async Task<bool> ControlPlaybackAsync(string action)
    {
        GlobalSystemMediaTransportControlsSession? session = null;
        try
        {
            session = (await GlobalSystemMediaTransportControlsSessionManager.RequestAsync()).GetCurrentSession();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A Windows without media sessions; the keys below still work.
        }

        if (session is not null)
        {
            try
            {
                var taken = action switch
                {
                    MediaCommand.Play => await session.TryPlayAsync(),
                    MediaCommand.Pause => await session.TryPauseAsync(),
                    MediaCommand.PlayPause => await session.TryTogglePlayPauseAsync(),
                    MediaCommand.Next => await session.TrySkipNextAsync(),
                    MediaCommand.Previous => await session.TrySkipPreviousAsync(),
                    MediaCommand.Stop => await session.TryStopAsync(),
                    _ => false,
                };
                if (taken)
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The app behind the session is going away; fall back to the key.
            }
        }

        // No session to tell, or one that would not listen. A key can only toggle, so "play" while something is
        // playing, and "pause" while nothing is, are left alone rather than turned into their opposite.
        var playing = session?.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        return action switch
        {
            MediaCommand.Play => playing || InputSimulator.Press(MediaKey.PlayPause),
            MediaCommand.Pause => !playing || InputSimulator.Press(MediaKey.PlayPause),
            MediaCommand.PlayPause => InputSimulator.Press(MediaKey.PlayPause),
            MediaCommand.Next => InputSimulator.Press(MediaKey.NextTrack),
            MediaCommand.Previous => InputSimulator.Press(MediaKey.PreviousTrack),
            MediaCommand.Stop => InputSimulator.Press(MediaKey.Stop),
            _ => false,
        };
    }

    private async Task ReportAsync(CancellationToken cancellationToken)
    {
        await _reporting.WaitAsync(cancellationToken);
        try
        {
            MediaPlayback playback;
            try
            {
                playback = await SystemMedia.ReadAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Nothing to say about what is playing; the volume is still worth reporting.
                playback = MediaPlayback.Nothing;
            }

            var sound = _speakers.TryRead();
            await _publisher.PublishAsync(
                EntityId,
                playback.Status,
                new Dictionary<string, object?>
                {
                    ["title"] = playback.Title,
                    ["artist"] = playback.Artist,
                    ["album"] = playback.Album,
                    ["app"] = playback.App,
                    ["volume"] = sound?.VolumePercent,
                    ["muted"] = sound?.IsMuted,
                },
                cancellationToken);
        }
        finally
        {
            _reporting.Release();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The media player could not do '{Action}'.")]
    private static partial void LogFailed(ILogger logger, string? action);
}
