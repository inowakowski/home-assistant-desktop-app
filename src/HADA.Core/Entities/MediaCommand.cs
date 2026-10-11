using System.Globalization;

namespace HADA.Core.Entities;

/// <summary>
/// What a <see cref="EntityKind.MediaPlayer"/> can be told: the <see cref="Models.ActionCommand.Value"/> of its
/// commands is one of these, and <see cref="Value"/> among the parameters says how loud, or whether muted.
/// </summary>
public static class MediaCommand
{
    public const string Play = "play";
    public const string Pause = "pause";
    public const string PlayPause = "play_pause";
    public const string Next = "next";
    public const string Previous = "previous";
    public const string Stop = "stop";

    /// <summary>Sets the volume to <see cref="Value"/>, a number from 0 to 100.</summary>
    public const string SetVolume = "volume_set";

    /// <summary>Mutes or unmutes, as <see cref="Value"/> says: <see cref="BinaryState.On"/> or <see cref="BinaryState.Off"/>.</summary>
    public const string Mute = "volume_mute";

    /// <summary>The parameter that goes with <see cref="SetVolume"/> and <see cref="Mute"/>.</summary>
    public const string Value = "value";

    /// <summary>Everything a media player may be told, in the order Home Assistant is told what it can do.</summary>
    public static IReadOnlyList<string> All { get; } = [Play, Pause, PlayPause, Next, Previous, Stop, SetVolume, Mute];

    /// <summary>
    /// Checks a command, and brings what goes with it into the form the media player expects: a whole number
    /// from 0 to 100 for the volume, <c>on</c> or <c>off</c> for muting, and nothing for the rest.
    /// </summary>
    public static bool TryRead(string? action, string? raw, out string? value)
    {
        value = null;
        switch (action)
        {
            case Play or Pause or PlayPause or Next or Previous or Stop:
                return true;
            case SetVolume:
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var volume) || volume is < 0 or > 100 || !double.IsFinite(volume))
                {
                    return false;
                }

                value = ((int)Math.Round(volume)).ToString(CultureInfo.InvariantCulture);
                return true;
            case Mute:
                value = raw?.Trim().ToLowerInvariant() switch
                {
                    BinaryState.On or "true" => BinaryState.On,
                    BinaryState.Off or "false" => BinaryState.Off,
                    _ => null,
                };
                return value is not null;
            default:
                return false;
        }
    }
}
