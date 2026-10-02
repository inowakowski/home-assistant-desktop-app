using HADA.Platform.Windows.Sensors;

namespace HADA.Tests.Platform;

/// <summary>
/// Smoke tests against the real Windows APIs. What they return depends on the machine (a focused window, an audio
/// device), so they check the contract; a wrong COM vtable layout would fail or return out-of-range values.
/// </summary>
public class SessionReaderTests
{
    [Fact]
    public void ForegroundWindow_title_fits_home_assistant_state_limit()
    {
        if (ForegroundWindow.TryRead() is { } window)
        {
            Assert.InRange(window.Title.Length, 0, ForegroundWindow.MaxTitleLength);
        }
    }

    [Fact]
    public void DefaultAudioEndpoint_reads_volume_in_range_or_reports_no_device()
    {
        using var endpoint = new DefaultAudioEndpoint();

        var first = endpoint.TryRead();
        var second = endpoint.TryRead();

        if (first is { } volume)
        {
            Assert.InRange(volume.VolumePercent, 0, 100);
            Assert.Equal(first, second);
        }
    }

    [Fact]
    public void Default_microphone_reads_level_in_range_or_reports_no_device()
    {
        using var endpoint = new DefaultAudioEndpoint(AudioDevice.Microphone);

        if (endpoint.TryRead() is { } microphone)
        {
            Assert.InRange(microphone.VolumePercent, 0, 100);
        }
    }
}
