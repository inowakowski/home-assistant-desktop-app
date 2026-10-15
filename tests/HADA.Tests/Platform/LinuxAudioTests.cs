using HADA.Core.Abstractions;
using HADA.Platform.Linux;

namespace HADA.Tests.Platform;

/// <summary>Sound on Linux, read and set through the sound server's tool, with what the tool would print.</summary>
public sealed class LinuxAudioTests
{
    [Theory]
    [InlineData("Volume: 0.40\n", 40, false)]
    [InlineData("Volume: 0.40 [MUTED]\n", 40, true)]
    [InlineData("Volume: 1.53\n", 100, false)]
    public void Wpctl_says_the_volume_and_whether_it_is_muted(string output, int percent, bool muted) =>
        Assert.Equal(new AudioLevel(percent, muted), LinuxAudioDevices.ParseWpctl(output));

    [Fact]
    public void Pactl_says_it_in_two_answers()
    {
        const string volume = "Volume: front-left: 26214 /  40% / -23.88 dB,   front-right: 26214 /  40% / -23.88 dB\n        balance 0.00\n";

        Assert.Equal(new AudioLevel(40, true), LinuxAudioDevices.ParsePactl(volume, "Mute: yes\n"));
        Assert.Equal(new AudioLevel(40, false), LinuxAudioDevices.ParsePactl(volume, "Mute: no\n"));
        Assert.Null(LinuxAudioDevices.ParsePactl("Failed to get sink information", "Mute: no"));
        Assert.Null(LinuxAudioDevices.ParseWpctl("Translate ID error: '@DEFAULT_AUDIO_SOURCE@' is not a valid ID\n"));
    }

    [Fact]
    public void A_reading_serves_the_sensor_and_the_controls_until_something_is_set()
    {
        var calls = new List<string>();
        var volume = "Volume: 0.40\n";
        var audio = new LinuxAudioDevices(pipeWire: true, (tool, arguments) =>
        {
            calls.Add(string.Join(' ', arguments));
            if (arguments[0] == "set-volume")
            {
                volume = "Volume: 0.65\n";
            }

            return new ToolResult(0, volume, string.Empty);
        });

        Assert.Equal(40, audio.Read(AudioEndpoint.Speakers)!.Value.VolumePercent);
        Assert.Equal(40, audio.Read(AudioEndpoint.Speakers)!.Value.VolumePercent);
        Assert.True(audio.TrySetVolume(65));
        Assert.Equal(65, audio.Read(AudioEndpoint.Speakers)!.Value.VolumePercent);

        Assert.Equal(["get-volume @DEFAULT_AUDIO_SINK@", "set-volume @DEFAULT_AUDIO_SINK@ 0.65", "get-volume @DEFAULT_AUDIO_SINK@"], calls);
    }

    [Fact]
    public void Muting_goes_to_the_device_it_is_meant_for()
    {
        var calls = new List<string>();
        var pipeWire = new LinuxAudioDevices(pipeWire: true, (tool, arguments) => Record(calls, tool, arguments));
        var pulse = new LinuxAudioDevices(pipeWire: false, (tool, arguments) => Record(calls, tool, arguments));

        pipeWire.TrySetMute(AudioEndpoint.Microphone, muted: true);
        pulse.TrySetMute(AudioEndpoint.Speakers, muted: false);
        pulse.TrySetMute(AudioEndpoint.Microphone, muted: true);
        pulse.TrySetVolume(30);

        Assert.Equal(
            [
                "wpctl set-mute @DEFAULT_AUDIO_SOURCE@ 1",
                "pactl set-sink-mute @DEFAULT_SINK@ 0",
                "pactl set-source-mute @DEFAULT_SOURCE@ 1",
                "pactl set-sink-volume @DEFAULT_SINK@ 30%",
            ],
            calls);
    }

    [Fact]
    public void Without_a_device_there_is_nothing_to_read_or_set()
    {
        var audio = new LinuxAudioDevices(pipeWire: true, (_, _) => new ToolResult(1, string.Empty, "no such device"));

        Assert.Null(audio.Read(AudioEndpoint.Microphone));
        Assert.False(audio.TrySetMute(AudioEndpoint.Microphone, muted: true));
    }

    private static ToolResult Record(List<string> calls, string tool, string[] arguments)
    {
        calls.Add(tool + " " + string.Join(' ', arguments));
        return new ToolResult(0, string.Empty, string.Empty);
    }
}
