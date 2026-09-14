using System.Runtime.InteropServices;
using HADA.Platform.Windows.Interop;

namespace HADA.Platform.Windows.Sensors;

public readonly record struct AudioVolumeInfo(int VolumePercent, bool IsMuted);

/// <summary>Reads the master volume of the default playback device through Core Audio.</summary>
public sealed class DefaultAudioEndpoint : IDisposable
{
    private const uint ClassContextAll = 0x17;

    private IMMDeviceEnumerator? _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

    /// <summary>
    /// Returns <see langword="null"/> when there is no active playback device.
    /// The default device is looked up on every call, so switching to headphones is picked up.
    /// </summary>
    public AudioVolumeInfo? TryRead()
    {
        ObjectDisposedException.ThrowIf(_enumerator is null, this);

        if (_enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out var device) != 0 || device is null)
        {
            return null;
        }

        object? activated = null;
        try
        {
            var iid = typeof(IAudioEndpointVolume).GUID;
            if (device.Activate(ref iid, ClassContextAll, 0, out activated) != 0 || activated is not IAudioEndpointVolume volume)
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

            Marshal.ReleaseComObject(device);
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
