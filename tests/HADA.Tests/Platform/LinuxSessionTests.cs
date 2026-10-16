using HADA.Core.Unix;
using HADA.Platform.Linux;

namespace HADA.Tests.Platform;

/// <summary>What the agent of Linux reads in the user's session, and how it starts at sign-in.</summary>
public sealed class LinuxSessionTests
{
    [Theory]
    [InlineData("t 1234\n", 1234)]
    [InlineData("u 60000", 60000)]
    public void Idle_time_is_read_from_the_desktops_answer(string output, int milliseconds) =>
        Assert.Equal(TimeSpan.FromMilliseconds(milliseconds), LinuxSessionReadings.ParseIdle(output));

    [Theory]
    [InlineData("")]
    [InlineData("s \"busy\"")]
    [InlineData("t -5")]
    public void An_answer_that_is_no_time_is_none(string output) =>
        Assert.Null(LinuxSessionReadings.ParseIdle(output));

    [Fact]
    public void The_desktop_that_answers_is_the_one_asked_from_then_on()
    {
        var asked = new List<string>();
        var readings = new LinuxSessionReadings((_, arguments) =>
        {
            asked.Add(arguments[2]);
            return arguments[2] == "org.freedesktop.ScreenSaver" ? new ToolResult(0, "u 2500\n", string.Empty) : new ToolResult(1, string.Empty, "no such service");
        });

        Assert.Equal(TimeSpan.FromMilliseconds(2500), readings.ReadIdleTime());
        Assert.Equal(TimeSpan.FromMilliseconds(2500), readings.ReadIdleTime());
        Assert.Equal(["org.gnome.Mutter.IdleMonitor", "org.freedesktop.ScreenSaver", "org.freedesktop.ScreenSaver"], asked);
    }

    [Fact]
    public void Where_no_desktop_answers_there_is_no_idle_time()
    {
        var readings = new LinuxSessionReadings((_, _) => null);

        Assert.Null(readings.ReadIdleTime());
    }

    [Fact]
    public void The_autostart_entry_starts_the_app_without_its_window()
    {
        var entry = LinuxAutostart.Entry("/opt/HADA app/HADA.App");

        Assert.Contains("[Desktop Entry]", entry, StringComparison.Ordinal);
        Assert.Contains("Exec=\"/opt/HADA app/HADA.App\" --background\n", entry, StringComparison.Ordinal);
        Assert.Contains("NoDisplay=true", entry, StringComparison.Ordinal);
    }

    [Fact]
    public void A_path_with_special_characters_is_escaped_as_desktop_entries_want()
    {
        // A dollar sign is escaped for the quoting and its backslash once more for the string: \\$.
        Assert.Equal("\"/home/ola/My \\\\$Apps/HADA.AppImage\"", LinuxAutostart.Quote("/home/ola/My $Apps/HADA.AppImage"));

        // A backslash takes four.
        Assert.Equal("\"/a\\\\\\\\b\"", LinuxAutostart.Quote("/a\\b"));
    }
}
