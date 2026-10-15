using HADA.Platform.Linux;

namespace HADA.Tests.Platform;

/// <summary>Which apps record on Linux: the camera by its device open in a process, the microphone by its recording streams.</summary>
public sealed class LinuxCaptureTests : IDisposable
{
    private readonly string _proc = Path.Combine(Path.GetTempPath(), "hada-proc-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_proc))
        {
            Directory.Delete(_proc, recursive: true);
        }
    }

    [Fact]
    public void The_apps_recording_are_named_by_their_streams()
    {
        const string output = """
            Source Output #41
            	Driver: PipeWire
            	Properties:
            		media.name = "Discord Voice"
            		application.name = "Discord"
            		application.process.binary = "Discord"
            Source Output #57
            	Properties:
            		application.name = "Firefox"
            Source Output #58
            	Properties:
            		application.name = "Firefox"
            """;

        Assert.Equal(["Discord", "Firefox"], LinuxSessionReadings.ParseRecordingApps(output));
        Assert.Empty(LinuxSessionReadings.ParseRecordingApps(string.Empty));
    }

    [Fact]
    public void Where_there_is_no_proc_the_camera_cannot_be_told() =>
        Assert.Null(LinuxSessionReadings.AppsWithOpen(Path.Combine(_proc, "missing"), _ => true));

    // Symbolic links, as /proc has them for open files, can be made by anyone on Linux only.
    [LinuxFact]
    public void The_apps_with_the_camera_open_are_found_in_proc()
    {
        Process("1200", "zoom", "/dev/video0", "/home/ola/notes.txt");
        Process("1300", "firefox", "/dev/snd/pcmC0D0c");
        Process("1400", "cheese", "/dev/video2");
        Directory.CreateDirectory(Path.Combine(_proc, "self"));

        Assert.Equal(["cheese", "zoom"], LinuxSessionReadings.AppsWithOpen(_proc, path => path.StartsWith("/dev/video", StringComparison.Ordinal)));
    }

    private void Process(string id, string name, params string[] openFiles)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_proc, id, "fd")).Parent!.FullName;
        File.WriteAllText(Path.Combine(folder, "comm"), name + "\n");
        for (var descriptor = 0; descriptor < openFiles.Length; descriptor++)
        {
            File.CreateSymbolicLink(Path.Combine(folder, "fd", descriptor.ToString(System.Globalization.CultureInfo.InvariantCulture)), openFiles[descriptor]);
        }
    }
}
