using System.Collections.Concurrent;
using System.Diagnostics;
using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Platform.Linux;
using Tmds.DBus.Protocol;

namespace HADA.Tests.Platform;

/// <summary>Media players on Linux, as they offer themselves through MPRIS on the session's D-Bus.</summary>
public sealed class LinuxMediaTests
{
    private const string Spotify = "org.mpris.MediaPlayer2.spotify";
    private const string Firefox = "org.mpris.MediaPlayer2.firefox.instance_1_42";

    [Fact]
    public void The_player_that_plays_counts_and_while_none_does_the_last_one_that_did()
    {
        var paused = new MprisPlayer(Spotify, MprisPlayer.Paused, "Song", null, null, "Spotify");
        var playing = new MprisPlayer(Firefox, MprisPlayer.Playing, "Video", null, null, "Mozilla Firefox");
        var stopped = new MprisPlayer(Firefox, MprisPlayer.Stopped, null, null, null, "Mozilla Firefox");

        Assert.Equal(playing, LinuxMedia.Choose([paused, playing], last: Spotify));
        Assert.Equal(stopped, LinuxMedia.Choose([paused, stopped], last: Firefox));
        Assert.Equal(paused, LinuxMedia.Choose([stopped, paused], last: null));
        Assert.Null(LinuxMedia.Choose([], last: Spotify));
    }

    [Fact]
    public void What_plays_is_read_from_the_metadata_and_the_artists_are_joined()
    {
        var properties = new Dictionary<string, VariantValue>
        {
            ["PlaybackStatus"] = "Playing",
            ["Metadata"] = Metadata("Song", ["First", "Second"], "Album"),
        };

        var playback = LinuxMedia.ParsePlayer(Spotify, properties, "Spotify").ToPlayback();

        Assert.Equal(new MediaPlayback(MediaPlayback.Playing, "Song", "First, Second", "Album", "Spotify"), playback);
    }

    [Fact]
    public void A_player_without_metadata_or_a_name_is_known_by_its_bus_name()
    {
        var playback = LinuxMedia.ParsePlayer(Firefox, new Dictionary<string, VariantValue> { ["PlaybackStatus"] = "Paused" }, identity: null).ToPlayback();

        Assert.Equal(new MediaPlayback(MediaPlayback.Paused, null, null, null, "firefox"), playback);
    }

    /// <summary>
    /// A real session bus, as <c>dbus-daemon</c> runs it for a desktop, with a player on it that says what it plays
    /// and writes down what it was told.
    /// </summary>
    [LinuxFact]
    public async Task A_player_on_the_bus_is_read_and_told_what_to_do()
    {
        const string daemon = "/usr/bin/dbus-daemon";
        if (!File.Exists(daemon))
        {
            // A Linux without D-Bus has nothing to test here; the build agents have it, and must run this.
            Assert.Null(Environment.GetEnvironmentVariable("CI"));
            return;
        }

        var folder = Directory.CreateTempSubdirectory("hada-dbus-");
        using var bus = Process.Start(new ProcessStartInfo(daemon)
        {
            ArgumentList = { "--session", "--nofork", "--nopidfile", "--print-address=1", $"--address=unix:path={Path.Combine(folder.FullName, "bus")}" },
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        try
        {
            var address = (await bus.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!.Trim();

            using var playerConnection = new DBusConnection(address);
            await playerConnection.ConnectAsync();
            var player = new FakePlayer();
            playerConnection.AddMethodHandler(player);
            await playerConnection.RequestNameAsync(Spotify, RequestNameOptions.Default);

            using var media = new LinuxMedia(address);
            Assert.Equal(new MediaPlayback(MediaPlayback.Playing, "Song", "First, Second", "Album", "Spotify"), await media.ReadAsync(CancellationToken.None));

            Assert.True(await media.ControlAsync(MediaCommand.Next, CancellationToken.None));
            Assert.True(await media.ControlAsync(MediaCommand.Pause, CancellationToken.None));
            Assert.Equal(["Next", "Pause"], player.Told);
            Assert.Equal(MediaPlayback.Paused, (await media.ReadAsync(CancellationToken.None)).Status);

            // A player that is gone is no longer told anything.
            await playerConnection.ReleaseNameAsync(Spotify);
            Assert.Equal(MediaPlayback.Nothing, await media.ReadAsync(CancellationToken.None));
            Assert.False(await media.ControlAsync(MediaCommand.Play, CancellationToken.None));
        }
        finally
        {
            bus.Kill();
            await bus.WaitForExitAsync();
            folder.Delete(recursive: true);
        }
    }

    private static VariantValue Metadata(string title, string[] artists, string album) =>
        new Dict<string, VariantValue>(new Dictionary<string, VariantValue>
        {
            ["mpris:trackid"] = VariantValue.ObjectPath(new ObjectPath("/org/mpris/MediaPlayer2/track/1")),
            ["xesam:title"] = title,
            ["xesam:artist"] = VariantValue.Array(artists),
            ["xesam:album"] = album,
        }).AsVariantValue();

    /// <summary>A player as MPRIS describes one: its properties, and the methods of its player interface.</summary>
    private sealed class FakePlayer : IPathMethodHandler
    {
        private readonly ConcurrentQueue<string> _told = new();
        private volatile string _status = "Playing";

        public string Path => "/org/mpris/MediaPlayer2";

        public bool HandlesChildPaths => false;

        public IReadOnlyList<string> Told => [.. _told];

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            var request = context.Request;
            switch (request.InterfaceAsString, request.MemberAsString)
            {
                case ("org.freedesktop.DBus.Properties", "GetAll"):
                    var properties = request.GetBodyReader().ReadString() == "org.mpris.MediaPlayer2.Player"
                        ? new Dictionary<string, VariantValue>
                        {
                            ["PlaybackStatus"] = _status,
                            ["Metadata"] = Metadata("Song", ["First", "Second"], "Album"),
                            ["CanGoNext"] = true,
                        }
                        : new Dictionary<string, VariantValue> { ["Identity"] = "Spotify" };
                    using (var writer = context.CreateReplyWriter("a{sv}"))
                    {
                        writer.WriteDictionary(properties);
                        context.Reply(writer.CreateMessage());
                    }

                    break;

                case ("org.mpris.MediaPlayer2.Player", { } member):
                    _told.Enqueue(member);
                    _status = member switch
                    {
                        "Pause" => "Paused",
                        "Play" => "Playing",
                        "Stop" => "Stopped",
                        _ => _status,
                    };
                    using (var writer = context.CreateReplyWriter(null))
                    {
                        context.Reply(writer.CreateMessage());
                    }

                    break;

                default:
                    context.ReplyUnknownMethodError();
                    break;
            }

            return ValueTask.CompletedTask;
        }
    }
}
