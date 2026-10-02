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

/// <summary>Reads and sets the volume and mute state of a default audio device through Core Audio.</summary>
public sealed class DefaultAudioEndpoint(AudioDevice device = AudioDevice.Speakers) : IDisposable
{
    private const uint ClassContextAll = 0x17;
    private const uint StorageRead = 0;

    // PKEY_Device_FriendlyName, e.g. "Speakers (Realtek Audio)".
    private static readonly DevicePropertyKey FriendlyName =
        new() { Category = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), Id = 14 };

    private IMMDeviceEnumerator? _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

    /// <summary>
    /// Returns <see langword="null"/> when there is no such device, e.g. no microphone is connected.
    /// The default device is looked up on every call, so switching to a headset is picked up.
    /// </summary>
    public AudioVolumeInfo? TryRead() => WithVolume<AudioVolumeInfo?>(volume =>
        volume.GetMasterVolumeLevelScalar(out var level) == 0 && volume.GetMute(out var muted) == 0
            ? new AudioVolumeInfo((int)MathF.Round(level * 100), muted)
            : null);

    /// <summary>Sets the volume, 0 to 100. Returns <see langword="false"/> when there is no such device or it refuses.</summary>
    public bool TrySetVolume(int percent) =>
        WithVolume(volume => volume.SetMasterVolumeLevelScalar(Math.Clamp(percent, 0, 100) / 100f, 0) == 0);

    public bool TrySetMute(bool muted) => WithVolume(volume => volume.SetMute(muted, 0) >= 0);

    /// <summary>The device's name as Windows shows it, or <see langword="null"/> when there is no such device.</summary>
    public string? TryReadName()
    {
        if (TryGetEndpoint() is not { } endpoint)
        {
            return null;
        }

        IPropertyStore? properties = null;
        try
        {
            if (endpoint.OpenPropertyStore(StorageRead, out properties) != 0
                || properties is null
                || properties.GetValue(FriendlyName, out var value) != 0)
            {
                return null;
            }

            try
            {
                return value.Type == PropVariant.TypeWideString ? Marshal.PtrToStringUni(value.Value) : null;
            }
            finally
            {
                _ = NativeMethods.PropVariantClear(ref value);
            }
        }
        finally
        {
            if (properties is not null)
            {
                Marshal.ReleaseComObject(properties);
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

    private IMMDevice? TryGetEndpoint()
    {
        ObjectDisposedException.ThrowIf(_enumerator is null, this);

        var (flow, role) = device == AudioDevice.Microphone
            ? (EDataFlow.Capture, ERole.Communications)
            : (EDataFlow.Render, ERole.Multimedia);
        return _enumerator.GetDefaultAudioEndpoint(flow, role, out var endpoint) == 0 ? endpoint : null;
    }

    private T? WithVolume<T>(Func<IAudioEndpointVolume, T?> use)
    {
        if (TryGetEndpoint() is not { } endpoint)
        {
            return default;
        }

        object? activated = null;
        try
        {
            var iid = typeof(IAudioEndpointVolume).GUID;
            return endpoint.Activate(ref iid, ClassContextAll, 0, out activated) == 0 && activated is IAudioEndpointVolume volume
                ? use(volume)
                : default;
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
}
