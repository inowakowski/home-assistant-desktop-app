using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Platform.Windows.Actions;
using HADA.Platform.Windows.Interop;
using Windows.Media.Control;

namespace HADA.Tray.Session;

/// <summary>
/// What Windows shows in its own media controls (the flyout above the volume slider), and the session they would
/// tell. Lives in the tray app: reading them needs the Windows Runtime, which only the tray app is built against.
/// </summary>
/// <remarks>
/// Commands go to that session, which tells play from pause; the multimedia keys, which can only toggle, are the
/// way out when there is no session to tell.
/// </remarks>
public sealed class WindowsMediaSessions : IMediaSessions
{
    public async Task<MediaPlayback> ReadAsync(CancellationToken cancellationToken)
    {
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        if (manager.GetCurrentSession() is not { } session)
        {
            return MediaPlayback.Nothing;
        }

        var status = session.GetPlaybackInfo()?.PlaybackStatus switch
        {
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => MediaPlayback.Playing,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => MediaPlayback.Paused,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => MediaPlayback.Stopped,
            _ => MediaPlayback.Idle,
        };

        var media = await session.TryGetMediaPropertiesAsync();
        return new MediaPlayback(
            status, NullIfEmpty(media?.Title), NullIfEmpty(media?.Artist), NullIfEmpty(media?.AlbumTitle), NullIfEmpty(session.SourceAppUserModelId));
    }

    public async Task<bool> ControlAsync(string action, CancellationToken cancellationToken)
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

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
