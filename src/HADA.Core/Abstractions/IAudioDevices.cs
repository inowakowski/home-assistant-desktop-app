namespace HADA.Core.Abstractions;

/// <summary>One of the session's default audio devices.</summary>
public enum AudioEndpoint
{
    /// <summary>The default playback device.</summary>
    Speakers,

    /// <summary>The default recording device, which is what a hardware microphone-mute key acts on.</summary>
    Microphone,
}

/// <param name="VolumePercent">0 to 100.</param>
public readonly record struct AudioLevel(int VolumePercent, bool IsMuted);

/// <summary>
/// The volume and mute state of the session's default audio devices, as each system keeps them. The default
/// device is looked up on every call, so switching to a headset is picked up.
/// </summary>
public interface IAudioDevices
{
    /// <summary>Returns <see langword="null"/> when there is no such device, e.g. no microphone is connected.</summary>
    AudioLevel? Read(AudioEndpoint endpoint);

    /// <summary>Sets the speakers' volume, 0 to 100. Returns <see langword="false"/> when there is no such device or it refuses.</summary>
    bool TrySetVolume(int percent);

    /// <summary>Returns <see langword="false"/> when there is no such device or it refuses.</summary>
    bool TrySetMute(AudioEndpoint endpoint, bool muted);
}
