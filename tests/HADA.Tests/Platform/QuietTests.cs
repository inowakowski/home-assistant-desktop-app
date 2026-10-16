using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using HADA.Core.Abstractions;
using HADA.Core.Actions;
using HADA.Core.Entities;
using HADA.Core.Messaging;
using HADA.Core.Models;
using HADA.Core.Unix;
using HADA.Platform.Linux;
using HADA.Platform.Windows.Sensors;
using Microsoft.Extensions.Logging.Abstractions;
using Tmds.DBus.Protocol;

namespace HADA.Tests.Platform;

/// <summary>"Do not disturb" switched from Home Assistant, on each system that lets it be.</summary>
public sealed class QuietTests : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly ChannelEventBus _bus = new();
    private readonly EntityRegistry _registry;

    public QuietTests() => _registry = new EntityRegistry(_bus);

    public ValueTask DisposeAsync() => _bus.DisposeAsync();

    [Fact]
    public async Task The_switch_turns_quiet_on_and_off_and_says_how_it_is()
    {
        var quiet = new FakeQuiet();
        await using var readings = _bus.Subscribe<TelemetryEvent>();
        using var control = new DoNotDisturbControl(_bus, _registry, NullLogger<DoNotDisturbControl>.Instance, quiet);
        await control.StartAsync(CancellationToken.None);

        Assert.True(_registry.TryGet(BuiltInEntityIds.SilenceNotifications, out var entity));
        Assert.Equal(EntityKind.Switch, entity.Kind);

        await _bus.PublishAsync(new ActionCommand { ActionId = BuiltInEntityIds.SilenceNotifications, Value = BinaryState.On });
        TelemetryEvent? reading = null;
        await WaitUntilAsync(() => readings.TryRead(out reading) && reading.State == BinaryState.On);
        Assert.Equal([true], quiet.Told);

        await control.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Where_quiet_cannot_be_switched_there_is_no_switch()
    {
        using var control = new DoNotDisturbControl(_bus, _registry, NullLogger<DoNotDisturbControl>.Instance, new FakeQuiet { CanSet = false });
        await control.StartAsync(CancellationToken.None);

        Assert.Empty(_registry.Entities);
        await control.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void Gnome_is_kept_quiet_by_switching_its_banners_off()
    {
        var calls = new List<string>();
        var banners = "true";
        var quiet = new LinuxDoNotDisturb(
            (tool, arguments) =>
            {
                calls.Add($"{tool} {string.Join(' ', arguments)}");
                if (arguments[0] == "set")
                {
                    banners = arguments[^1];
                }

                return new ToolResult(0, banners + "\n", string.Empty);
            },
            desktop: "ubuntu:GNOME");

        Assert.True(quiet.CanSet);
        Assert.False(quiet.Read());
        Assert.Null(quiet.Set(true));
        Assert.Equal("gsettings set org.gnome.desktop.notifications show-banners false", calls[^1]);
        Assert.True(quiet.Read());
    }

    [Fact]
    public void A_desktop_whose_notifications_cannot_be_held_back_has_no_switch()
    {
        var quiet = new LinuxDoNotDisturb((_, _) => new ToolResult(1, string.Empty, "unknown"), desktop: "sway");

        Assert.False(quiet.CanSet);
        Assert.Null(quiet.Read());
        Assert.NotNull(quiet.Set(true));
    }

    /// <summary>
    /// KDE's way, on a real session bus with a notification server that holds notifications back while asked to:
    /// HADA asks, and lets go when told to.
    /// </summary>
    [LinuxFact]
    public async Task Kde_holds_notifications_back_while_hada_asks_it_to()
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
            using var serverConnection = new DBusConnection(address);
            await serverConnection.ConnectAsync();
            var server = new FakeNotificationServer();
            serverConnection.AddMethodHandler(server);
            await serverConnection.RequestNameAsync("org.freedesktop.Notifications", RequestNameOptions.Default);

            // The property is read with busctl, which the test answers from the server it runs.
            using var quiet = new LinuxDoNotDisturb(
                (_, _) => new ToolResult(0, server.Inhibited ? "b true\n" : "b false\n", string.Empty),
                desktop: "KDE",
                busAddress: address);

            Assert.True(quiet.CanSet);
            Assert.Null(quiet.Set(true));
            Assert.True(server.Inhibited);
            Assert.True(quiet.Read());

            Assert.Null(quiet.Set(false));
            Assert.False(server.Inhibited);
            Assert.Equal(["Inhibit hada", "UnInhibit"], server.Told);
        }
        finally
        {
            bus.Kill();
            await bus.WaitForExitAsync();
            folder.Delete(recursive: true);
        }
    }

    /// <summary>Windows' quiet hours: told and switchable where the Settings app's switch is.</summary>
    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void Windows_says_whether_it_is_quiet_and_lets_it_be_switched()
    {
        var quiet = new WindowsDoNotDisturb();

        Assert.True(quiet.CanSet);
        Assert.NotNull(quiet.Read());
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        while (!condition())
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    private sealed class FakeQuiet : IDoNotDisturb
    {
        private readonly ConcurrentQueue<bool> _told = new();
        private volatile bool _on;

        public bool CanSet { get; set; } = true;

        public IReadOnlyList<bool> Told => [.. _told];

        public bool? Read() => _on;

        public string? Set(bool on)
        {
            _told.Enqueue(on);
            _on = on;
            return null;
        }
    }

    /// <summary>The part of a notification server that holds notifications back: Inhibit and UnInhibit.</summary>
    private sealed class FakeNotificationServer : IPathMethodHandler
    {
        private readonly ConcurrentQueue<string> _told = new();
        private int _holds;

        public string Path => "/org/freedesktop/Notifications";

        public bool HandlesChildPaths => false;

        public bool Inhibited => Volatile.Read(ref _holds) > 0;

        public IReadOnlyList<string> Told => [.. _told];

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            var request = context.Request;
            switch (request.MemberAsString)
            {
                case "Inhibit":
                    var app = request.GetBodyReader().ReadString();
                    _told.Enqueue($"Inhibit {app}");
                    Interlocked.Increment(ref _holds);
                    using (var writer = context.CreateReplyWriter("u"))
                    {
                        writer.WriteUInt32(7);
                        context.Reply(writer.CreateMessage());
                    }

                    break;

                case "UnInhibit":
                    _told.Enqueue("UnInhibit");
                    Interlocked.Decrement(ref _holds);
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
