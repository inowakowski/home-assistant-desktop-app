using System.Collections.Concurrent;
using HADA.Core.Entities;
using HADA.Core.Input;
using HADA.Core.Messaging;
using HADA.Core.Models;
using HADA.Platform.Windows.Actions;
using HADA.Platform.Windows.Sensors;
using Microsoft.Extensions.Logging.Abstractions;

namespace HADA.Tests.Platform;

/// <summary>
/// What Home Assistant can make the computer do. Nothing here switches the computer off, presses a key or changes
/// the volume: the tests stop where the real effect would begin.
/// </summary>
public sealed class ActionTests : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly ChannelEventBus _bus = new();
    private readonly EntityRegistry _registry;

    public ActionTests() => _registry = new EntityRegistry(_bus);

    public ValueTask DisposeAsync() => _bus.DisposeAsync();

    [Fact]
    public async Task Power_buttons_are_off_by_default_and_do_what_their_name_says()
    {
        var power = new FakePower();
        using var actions = new PowerActions(_bus, _registry, NullLogger<PowerActions>.Instance, power);
        await actions.StartAsync(CancellationToken.None);

        Assert.Equal(
            ["hibernate", "restart", "shutdown", "sleep"],
            _registry.Entities.Select(entity => entity.Id).Order(StringComparer.Ordinal));
        Assert.All(_registry.Entities, entity =>
        {
            Assert.Equal(EntityKind.Button, entity.Kind);
            Assert.False(entity.EnabledByDefault);
        });

        await _bus.PublishAsync(new ActionCommand { ActionId = "lock_screen" });
        await _bus.PublishAsync(new ActionCommand { ActionId = PowerActions.RestartEntityId });
        await _bus.PublishAsync(new ActionCommand { ActionId = PowerActions.SleepEntityId });

        await WaitUntilAsync(() => power.Executed.Count == 2);
        Assert.Equal([PowerCommand.Sleep, PowerCommand.Restart], power.Executed.Order());

        await actions.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void The_privilege_that_sleeping_needs_can_be_enabled()
    {
        Assert.Equal(0, WindowsPowerControl.EnableShutdownPrivilege());
    }

    [Fact]
    public void Input_can_be_injected()
    {
        // Session 0 (a service, or a build agent) has no desktop that takes input.
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        if (process.SessionId == 0 || !Environment.UserInteractive)
        {
            return;
        }

        // Moving the pointer by nothing is harmless, but fails like any other input if the structures are laid out wrong.
        Assert.True(InputSimulator.NudgeMouse(pixels: 0));

        // So is a Shift pressed and released by itself; it goes the way of every key combination.
        Assert.True(InputSimulator.Press(new KeyCombination(KeyModifiers.Shift, 0)));
        Assert.False(InputSimulator.Press(default(KeyCombination)));
    }

    [Theory]
    [InlineData("notepad", "notepad", "")]
    [InlineData("notepad notes.txt", "notepad", "notes.txt")]
    [InlineData("  https://example.com/page?a=1  ", "https://example.com/page?a=1", "")]
    [InlineData("\"C:\\Program Files\\App\\app.exe\" --mode  fast", "C:\\Program Files\\App\\app.exe", "--mode  fast")]
    [InlineData("\"C:\\Program Files\\App\\app.exe\"", "C:\\Program Files\\App\\app.exe", "")]
    [InlineData("\"unterminated", "unterminated", "")]
    public void A_command_line_is_split_into_what_to_start_and_its_arguments(string commandLine, string file, string arguments)
    {
        Assert.Equal((file, arguments), LaunchAction.Split(commandLine));
    }

    [Fact]
    public void An_unquoted_path_with_spaces_is_taken_whole_when_it_exists()
    {
        var folder = Path.Combine(Path.GetTempPath(), "HADA tests " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Assert.Equal((folder, string.Empty), LaunchAction.Split(folder));
        }
        finally
        {
            Directory.Delete(folder);
        }
    }

    [Fact]
    public async Task Volume_and_mute_controls_are_registered_with_the_kinds_home_assistant_needs()
    {
        using var audio = new AudioControl(_bus, _registry, NullLogger<AudioControl>.Instance);
        await audio.StartAsync(CancellationToken.None);

        Assert.True(_registry.TryGet(AudioControl.VolumeEntityId, out var volume));
        Assert.Equal(EntityKind.Number, volume.Kind);
        Assert.Equal((0d, 100d), (volume.Min, volume.Max));
        Assert.True(_registry.TryGet(AudioControl.MuteEntityId, out var mute));
        Assert.Equal(EntityKind.Switch, mute.Kind);
        Assert.True(_registry.TryGet(AudioControl.MicrophoneMuteEntityId, out _));

        await audio.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void The_default_audio_device_has_a_name_when_there_is_one()
    {
        using var speakers = new DefaultAudioEndpoint();

        // A build agent may have no sound card at all.
        if (speakers.TryRead() is not null)
        {
            Assert.False(string.IsNullOrWhiteSpace(speakers.TryReadName()));
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        while (!condition())
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    private sealed class FakePower : IPowerControl
    {
        public ConcurrentBag<PowerCommand> Executed { get; } = [];

        public string? Execute(PowerCommand command)
        {
            Executed.Add(command);
            return null;
        }
    }
}
