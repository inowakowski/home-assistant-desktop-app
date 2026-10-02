using System.Runtime.InteropServices;
using HADA.Platform.Windows.Interop;

namespace HADA.Platform.Windows.Sensors;

public readonly record struct AudioVolumeInfo(int VolumePercent, bool IsMuted);

public enum AudioDevice
{
    /// <summary>The default playback device.</summary>
    Speakers,

    /// <summary>The default recording device for calls, which is what a hardware microphone-mute key acts on.</summary>
    Microphone,
}

/// <summary>Reads the volume and mute state of a default audio device through Core Audio.</summary>
public sealed class DefaultAudioEndpoint(AudioDevice device = AudioDevice.Speakers) : IDisposable
{
    private const uint ClassContextAll = 0x17;

    private IMMDeviceEnumerator? _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

    /// <summary>
    /// Returns <see langword="null"/> when there is no such device, e.g. no microphone is connected.
    /// The default device is looked up on every call, so switching to a headset is picked up.
    /// </summary>
    public AudioVolumeInfo? TryRead()
    {
        ObjectDisposedException.ThrowIf(_enumerator is null, this);

        var (flow, role) = device == AudioDevice.Microphone
            ? (EDataFlow.Capture, ERole.Communications)
            : (EDataFlow.Render, ERole.Multimedia);
        if (_enumerator.GetDefaultAudioEndpoint(flow, role, out var endpoint) != 0 || endpoint is null)
        {
            return null;
        }

        object? activated = null;
        try
        {
            var iid = typeof(IAudioEndpointVolume).GUID;
            if (endpoint.Activate(ref iid, ClassContextAll, 0, out activated) != 0 || activated is not IAudioEndpointVolume volume)
            {
                return null;
            }

            if (volume.GetMasterVolumeLevelScalar(out var level) != 0 || volume.GetMute(out var muted) != 0)
            {
                return null;
            }

            return new AudioVolumeInfo((int)MathF.Round(level * 100), muted);
        }
        finally
        {
            if (activated is not null)
            {
                Marshal.ReleaseComObject(activated);
            }

            Marshal.ReleaseComObject(endpoint);
        }
    }

    public void Dispose()
    {
        if (_enumerator is not null)
        {
            Marshal.ReleaseComObject(_enumerator);
            _enumerator = null;
        }
    }
}
