namespace HADA.Core.Abstractions;

/// <param name="Status"><c>playing</c>, <c>paused</c>, <c>stopped</c> or <c>idle</c> when no app offers media controls.</param>
/// <param name="App">The app the media belongs to, as the system names it, e.g. <c>Spotify.exe</c> on Windows or <c>Spotify</c> on Linux.</param>
public sealed record MediaPlayback(string Status, string? Title, string? Artist, string? Album, string? App)
{
    public const string Playing = "playing";
    public const string Paused = "paused";
    public const string Stopped = "stopped";
    public const string Idle = "idle";

    public static MediaPlayback Nothing { get; } = new(Idle, null, null, null, null);
}

/// <summary>
/// The media the user's session plays, as the system's own media controls show it: Windows' flyout above the
/// volume slider, the media controls of GNOME and KDE. Commands go to the player those controls would tell.
/// </summary>
public interface IMediaSessions
{
    /// <summary>What is playing right now; <see cref="MediaPlayback.Nothing"/> when no app offers media controls.</summary>
    /// <exception cref="Exception">The system's media controls cannot be asked right now, e.g. while a player is closing.</exception>
    Task<MediaPlayback> ReadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Tells the player one of <see cref="Entities.MediaCommand"/>'s playback commands: play, pause, play/pause,
    /// next, previous or stop. Returns <see langword="false"/> when there is no player to tell, or it would not listen.
    /// </summary>
    Task<bool> ControlAsync(string action, CancellationToken cancellationToken);
}
