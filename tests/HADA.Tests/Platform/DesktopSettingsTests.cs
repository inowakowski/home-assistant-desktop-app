using System.Runtime.Versioning;
using HADA.Core.Abstractions;
using HADA.Core.Actions;
using HADA.Core.Entities;
using HADA.Core.Messaging;
using HADA.Core.Models;
using HADA.Core.Sensors;
using HADA.Core.Unix;
using HADA.Platform.Linux;
using HADA.Platform.MacOS;
using HADA.Platform.Windows.Actions;
using Microsoft.Extensions.Logging.Abstractions;

namespace HADA.Tests.Platform;

/// <summary>A full screen, the dark look and signing out, on each system that has them.</summary>
public sealed class DesktopSettingsTests : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly ChannelEventBus _bus = new();
    private readonly EntityRegistry _registry;

    public DesktopSettingsTests() => _registry = new EntityRegistry(_bus);

    public ValueTask DisposeAsync() => _bus.DisposeAsync();

    [Fact]
    public async Task A_full_screen_is_on_with_what_fills_it()
    {
        await using var readings = _bus.Subscribe<TelemetryEvent>();
        using var sensor = new FullScreenSensor(_bus, _registry, new FakeFullScreen(FullScreenKind.Game));
        await sensor.StartAsync(CancellationToken.None);

        var reading = await FirstAsync(readings, BuiltInEntityIds.FullScreenApp);

        Assert.Equal(BinaryState.On, reading.State);
        Assert.Equal("game", reading.Attributes["kind"]);
        await sensor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Where_the_system_does_not_tell_there_is_no_full_screen_sensor()
    {
        using var sensor = new FullScreenSensor(_bus, _registry, new FakeFullScreen(null));
        await sensor.StartAsync(CancellationToken.None);
        await sensor.StopAsync(CancellationToken.None);

        Assert.Empty(_registry.Entities);
    }

    [Fact]
    public async Task The_dark_look_is_switched_and_reported()
    {
        var darkMode = new FakeDarkMode();
        await using var readings = _bus.Subscribe<TelemetryEvent>();
        using var control = new DarkModeControl(_bus, _registry, NullLogger<DarkModeControl>.Instance, darkMode);
        await control.StartAsync(CancellationToken.None);
        Assert.Equal(BinaryState.Off, (await FirstAsync(readings, BuiltInEntityIds.DarkMode)).State);

        await _bus.PublishAsync(new ActionCommand { ActionId = BuiltInEntityIds.DarkMode, Value = BinaryState.On });

        Assert.Equal(BinaryState.On, (await FirstAsync(readings, BuiltInEntityIds.DarkMode)).State);
        Assert.True(darkMode.Dark);
        await control.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Signing_out_is_a_button_that_is_off_until_switched_on()
    {
        var signOut = new FakeSignOut();
        using var action = new SignOutAction(_bus, _registry, NullLogger<SignOutAction>.Instance, signOut);
        await action.StartAsync(CancellationToken.None);

        Assert.True(_registry.TryGet(BuiltInEntityIds.SignOut, out var entity));
        Assert.Equal(EntityKind.Button, entity.Kind);
        Assert.False(entity.EnabledByDefault);

        await _bus.PublishAsync(new ActionCommand { ActionId = BuiltInEntityIds.SignOut, Value = string.Empty });
        await WaitUntilAsync(() => signOut.Times == 1);
        await action.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Desktops_that_cannot_be_switched_or_signed_out_get_neither()
    {
        using var control = new DarkModeControl(_bus, _registry, NullLogger<DarkModeControl>.Instance, new FakeDarkMode { CanSet = false });
        using var action = new SignOutAction(_bus, _registry, NullLogger<SignOutAction>.Instance, new FakeSignOut { CanSignOut = false });
        await control.StartAsync(CancellationToken.None);
        await action.StartAsync(CancellationToken.None);

        Assert.Empty(_registry.Entities);
        await control.StopAsync(CancellationToken.None);
        await action.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData("'prefer-dark'\n", true)]
    [InlineData("'default'\n", false)]
    [InlineData("'prefer-light'\n", false)]
    [InlineData("No such key “color-scheme”", null)]
    public void Gnomes_colour_scheme_says_whether_it_is_dark(string output, bool? dark) =>
        Assert.Equal(dark, LinuxDarkMode.ParseGnomeScheme(output));

    [Fact]
    public void Gnome_is_switched_to_dark_and_back_through_its_colour_scheme()
    {
        var calls = new List<string>();
        var darkMode = new LinuxDarkMode(
            (tool, arguments) =>
            {
                calls.Add($"{tool} {string.Join(' ', arguments)}");
                return new ToolResult(0, "'default'\n", string.Empty);
            },
            "ubuntu:GNOME");

        Assert.True(darkMode.CanSet);
        Assert.False(darkMode.Read());
        Assert.Null(darkMode.Set(dark: true));
        Assert.Equal("gsettings set org.gnome.desktop.interface color-scheme prefer-dark", calls[^1]);
        Assert.Null(darkMode.Set(dark: false));
        Assert.Equal("gsettings set org.gnome.desktop.interface color-scheme default", calls[^1]);
    }

    [Fact]
    public void Linux_signs_out_through_gnome_or_kde_and_nowhere_else()
    {
        var calls = new List<string>();
        ToolResult? Run(string tool, string[] arguments)
        {
            calls.Add($"{tool} {string.Join(' ', arguments)}");

            // Plasma 6's service is missing here, so Plasma 5's session manager is asked.
            return new ToolResult(arguments.Contains("--dest=org.kde.Shutdown") ? 1 : 0, string.Empty, string.Empty);
        }

        Assert.Null(new LinuxSignOut(Run, "ubuntu:GNOME").SignOut());
        Assert.Equal("gnome-session-quit --logout --no-prompt", calls[^1]);

        Assert.Null(new LinuxSignOut(Run, "KDE").SignOut());
        Assert.Contains("org.kde.KSMServerInterface.logout", calls[^1], StringComparison.Ordinal);

        Assert.False(new LinuxSignOut(Run, "XFCE").CanSignOut);
    }

    [Fact]
    public void The_mac_reads_its_look_from_the_defaults_and_switches_it_through_system_events()
    {
        string[]? told = null;
        var dark = new MacDarkMode((tool, arguments) =>
        {
            told = tool == "osascript" ? arguments : told;
            return new ToolResult(0, "Dark\n", string.Empty);
        });
        Assert.True(dark.Read());
        Assert.Null(dark.Set(dark: false));
        Assert.NotNull(told);
        Assert.Contains("set dark mode to false", told[1], StringComparison.Ordinal);

        // Without the setting, defaults says it does not exist: the light look.
        Assert.False(new MacDarkMode((_, _) => new ToolResult(1, string.Empty, "does not exist")).Read());
        Assert.Null(new MacDarkMode((_, _) => null).Read());

        string[]? signedOut = null;
        Assert.Null(new MacSignOut((_, arguments) =>
        {
            signedOut = arguments;
            return new ToolResult(0, string.Empty, string.Empty);
        }).SignOut());
        Assert.NotNull(signedOut);
        Assert.Contains("loginwindow", signedOut[1], StringComparison.Ordinal);
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void Windows_tells_whether_an_app_fills_the_screen_and_how_it_looks()
    {
        Assert.NotNull(new WindowsFullScreen().Read());
        Assert.NotNull(new WindowsDarkMode().Read());
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void An_x11_desktop_with_no_window_in_front_has_nothing_full_screen()
    {
        using var windows = X11ActiveWindow.Open();
        if (windows is null)
        {
            return;
        }

        Assert.False(windows.ReadFullScreen());
    }

    private static async Task<TelemetryEvent> FirstAsync(IEventSubscription<TelemetryEvent> readings, string id)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        return await readings.ReadAllAsync(timeout.Token).FirstAsync(reading => reading.SensorId == id, timeout.Token);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        while (!condition())
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    private sealed class FakeFullScreen(FullScreenKind? kind) : IFullScreen
    {
        public FullScreenKind? Read() => kind;
    }

    private sealed class FakeDarkMode : IDarkMode
    {
        public bool Dark { get; private set; }

        public bool CanSet { get; init; } = true;

        public bool? Read() => Dark;

        public string? Set(bool dark)
        {
            Dark = dark;
            return null;
        }
    }

    private sealed class FakeSignOut : ISignOut
    {
        private int _times;

        public int Times => Volatile.Read(ref _times);

        public bool CanSignOut { get; init; } = true;

        public string? SignOut()
        {
            Interlocked.Increment(ref _times);
            return null;
        }
    }
}
