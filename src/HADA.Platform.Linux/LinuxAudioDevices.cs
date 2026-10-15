using System.Globalization;
using HADA.Core.Abstractions;

namespace HADA.Platform.Linux;

/// <summary>
/// The session's default audio devices through the sound server's own tool: <c>wpctl</c> of PipeWire, which most
/// distributions have now, else <c>pactl</c>, which PulseAudio and PipeWire's stand-in for it both answer.
/// </summary>
/// <remarks>
/// Read by a sensor and by the controls every second; a reading stays good for a moment, so the tool runs once
/// for both.
/// </remarks>
public sealed class LinuxAudioDevices : IAudioDevices
{
    private static readonly TimeSpan Fresh = TimeSpan.FromMilliseconds(1500);

    private readonly Func<string, string[], ToolResult?> _run;
    private readonly bool _pipeWire;
    private readonly Lock _lock = new();
    private readonly Dictionary<AudioEndpoint, (DateTime At, AudioLevel? Level)> _last = [];

    /// <param name="pipeWire">Whether <c>wpctl</c> is there; otherwise <c>pactl</c> is asked.</param>
    /// <param name="run">Runs a tool; replaced in tests.</param>
    public LinuxAudioDevices(bool pipeWire, Func<string, string[], ToolResult?>? run = null)
    {
        _pipeWire = pipeWire;
        _run = run ?? Tools.Run;
    }

    /// <summary>The tool this system has for it; <see langword="null"/> where it has neither, and HADA has no sound controls there.</summary>
    public static LinuxAudioDevices? ForThisSystem() =>
        Tools.Find("wpctl") is not null ? new LinuxAudioDevices(pipeWire: true)
        : Tools.Find("pactl") is not null ? new LinuxAudioDevices(pipeWire: false)
        : null;

    public AudioLevel? Read(AudioEndpoint endpoint)
    {
        lock (_lock)
        {
            if (_last.TryGetValue(endpoint, out var last) && DateTime.UtcNow - last.At < Fresh)
            {
                return last.Level;
            }

            var level = _pipeWire ? ReadWithWpctl(endpoint) : ReadWithPactl(endpoint);
            _last[endpoint] = (DateTime.UtcNow, level);
            return level;
        }
    }

    public string? ReadName(AudioEndpoint endpoint)
    {
        if (_pipeWire)
        {
            return _run("wpctl", ["inspect", Device(endpoint)]) is { Succeeded: true } result ? ParseWpctlName(result.Output) : null;
        }

        var (defaultVerb, listVerb) = endpoint == AudioEndpoint.Microphone ? ("get-default-source", "sources") : ("get-default-sink", "sinks");
        return _run("pactl", [defaultVerb]) is { Succeeded: true } device
            && device.Output.Trim() is { Length: > 0 } name
            && _run("pactl", ["list", listVerb]) is { Succeeded: true } list
            ? ParsePactlName(list.Output, name)
            : null;
    }

    /// <summary>
    /// Reads the description out of <c>wpctl inspect</c>, whose lines are properties, those that identify the
    /// device starred: <c>  * node.description = "Built-in Audio Analog Stereo"</c>.
    /// </summary>
    public static string? ParseWpctlName(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            var property = line.Trim().TrimStart('*').TrimStart();
            if (property.StartsWith("node.description", StringComparison.Ordinal) && property.IndexOf('=', StringComparison.Ordinal) is > 0 and var equals)
            {
                return property[(equals + 1)..].Trim().Trim('"') is { Length: > 0 } description ? description : null;
            }
        }

        return null;
    }

    /// <summary>
    /// Finds the description of the device named <paramref name="device"/> in <c>pactl list sinks</c> or
    /// <c>sources</c>: a block for each device, its <c>Name:</c> followed by its <c>Description:</c>. What records
    /// what a sink plays, its <c>.monitor</c>, is no microphone.
    /// </summary>
    public static string? ParsePactlName(string output, string device)
    {
        if (device.EndsWith(".monitor", StringComparison.Ordinal))
        {
            return null;
        }

        var found = false;
        foreach (var line in output.Split('\n'))
        {
            var text = line.Trim();
            if (text.StartsWith("Name:", StringComparison.Ordinal))
            {
                found = text["Name:".Length..].Trim() == device;
            }
            else if (found && text.StartsWith("Description:", StringComparison.Ordinal))
            {
                return text["Description:".Length..].Trim() is { Length: > 0 } description ? description : null;
            }
        }

        return null;
    }

    public bool TrySetVolume(int percent)
    {
        var level = Math.Clamp(percent, 0, 100);
        return Changed(
            AudioEndpoint.Speakers,
            _pipeWire
                ? _run("wpctl", ["set-volume", Device(AudioEndpoint.Speakers), (level / 100d).ToString("0.00", CultureInfo.InvariantCulture)])
                : _run("pactl", ["set-sink-volume", "@DEFAULT_SINK@", level.ToString(CultureInfo.InvariantCulture) + "%"]));
    }

    public bool TrySetMute(AudioEndpoint endpoint, bool muted)
    {
        var state = muted ? "1" : "0";
        return Changed(
            endpoint,
            _pipeWire
                ? _run("wpctl", ["set-mute", Device(endpoint), state])
                : _run("pactl", [endpoint == AudioEndpoint.Microphone ? "set-source-mute" : "set-sink-mute", PulseDevice(endpoint), state]));
    }

    /// <summary>Reads <c>wpctl get-volume</c>: <c>Volume: 0.40</c>, with <c>[MUTED]</c> after it when muted.</summary>
    public static AudioLevel? ParseWpctl(string output)
    {
        var parts = output.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && parts[0] == "Volume:"
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var volume)
            ? new AudioLevel(Percent(volume * 100), parts.Contains("[MUTED]"))
            : null;
    }

    /// <summary>
    /// Reads <c>pactl get-sink-volume</c>, whose first percentage is the first channel's
    /// (<c>Volume: front-left: 26214 /  40% / -23.88 dB, ...</c>), and <c>pactl get-sink-mute</c> (<c>Mute: yes</c>).
    /// </summary>
    public static AudioLevel? ParsePactl(string volume, string mute)
    {
        var percent = volume.IndexOf('%', StringComparison.Ordinal);
        if (percent < 0 || !mute.TrimStart().StartsWith("Mute:", StringComparison.Ordinal))
        {
            return null;
        }

        var start = volume.LastIndexOf('/', percent) + 1;
        return double.TryParse(volume.AsSpan(start, percent - start).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var level)
            ? new AudioLevel(Percent(level), mute.Contains("yes", StringComparison.Ordinal))
            : null;
    }

    // Raised above 100 % by an app or the desktop, it is reported as all there is, as the control's range ends there.
    private static int Percent(double value) => (int)Math.Clamp(Math.Round(value), 0, 100);

    private static string Device(AudioEndpoint endpoint) =>
        endpoint == AudioEndpoint.Microphone ? "@DEFAULT_AUDIO_SOURCE@" : "@DEFAULT_AUDIO_SINK@";

    private static string PulseDevice(AudioEndpoint endpoint) =>
        endpoint == AudioEndpoint.Microphone ? "@DEFAULT_SOURCE@" : "@DEFAULT_SINK@";

    private AudioLevel? ReadWithWpctl(AudioEndpoint endpoint) =>
        _run("wpctl", ["get-volume", Device(endpoint)]) is { Succeeded: true } result ? ParseWpctl(result.Output) : null;

    private AudioLevel? ReadWithPactl(AudioEndpoint endpoint)
    {
        var (volumeVerb, muteVerb) = endpoint == AudioEndpoint.Microphone
            ? ("get-source-volume", "get-source-mute")
            : ("get-sink-volume", "get-sink-mute");
        return _run("pactl", [volumeVerb, PulseDevice(endpoint)]) is { Succeeded: true } volume
            && _run("pactl", [muteVerb, PulseDevice(endpoint)]) is { Succeeded: true } mute
            ? ParsePactl(volume.Output, mute.Output)
            : null;
    }

    /// <summary>Whether the tool did it; the next reading asks the device again, to show the new state at once.</summary>
    private bool Changed(AudioEndpoint endpoint, ToolResult? result)
    {
        lock (_lock)
        {
            _last.Remove(endpoint);
        }

        return result is { Succeeded: true };
    }
}
