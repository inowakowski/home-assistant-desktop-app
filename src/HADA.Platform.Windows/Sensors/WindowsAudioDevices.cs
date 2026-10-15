using System.Runtime.InteropServices;
using HADA.Core.Abstractions;

namespace HADA.Platform.Windows.Sensors;

/// <summary>The session's default audio devices through Core Audio.</summary>
public sealed class WindowsAudioDevices : IAudioDevices, IDisposable
{
    private readonly DefaultAudioEndpoint _speakers = new();
    private readonly DefaultAudioEndpoint _microphone = new(AudioDevice.Microphone);

    public AudioLevel? Read(AudioEndpoint endpoint)
    {
        try
        {
            return Of(endpoint).TryRead() is { } level ? new AudioLevel(level.VolumePercent, level.IsMuted) : null;
        }
        catch (COMException)
        {
            // A device that went away while being read.
            return null;
        }
    }

    public bool TrySetVolume(int percent) => _speakers.TrySetVolume(percent);

    public bool TrySetMute(AudioEndpoint endpoint, bool muted) => Of(endpoint).TrySetMute(muted);

    public void Dispose()
    {
        _speakers.Dispose();
        _microphone.Dispose();
    }

    private DefaultAudioEndpoint Of(AudioEndpoint endpoint) => endpoint == AudioEndpoint.Microphone ? _microphone : _speakers;
}
