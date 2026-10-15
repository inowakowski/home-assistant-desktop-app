using System.Collections.Concurrent;
using System.Runtime.Versioning;
using HADA.Core.Abstractions;
using HADA.Core.Actions;
using HADA.Core.Input;
using HADA.Core.Messaging;
using HADA.Core.Models;
using HADA.Platform.Linux;
using Microsoft.Extensions.Logging.Abstractions;

namespace HADA.Tests.Platform;

/// <summary>Keys pressed for the service's buttons, on Linux's X11 and wherever there is a keyboard to press them on.</summary>
public sealed class LinuxInputTests
{
    [Theory]
    [InlineData("A", 0x61UL)]
    [InlineData("7", 0x37UL)]
    [InlineData("F1", 0xFFBEUL)]
    [InlineData("F24", 0xFFD5UL)]
    [InlineData("Enter", 0xFF0DUL)]
    [InlineData("Delete", 0xFFFFUL)]
    [InlineData("MediaPlayPause", 0x1008FF14UL)]
    [InlineData("VolumeUp", 0x1008FF13UL)]
    public void A_key_is_the_x11_key_of_the_same_name(string name, ulong keysym)
    {
        Assert.True(KeyCombination.TryParse(name, out var combination));

        Assert.Equal([keysym], X11Keys.KeysymsOf(combination));
    }

    [Fact]
    public void Modifiers_come_before_the_key()
    {
        Assert.True(KeyCombination.TryParse("Ctrl+Alt+Shift+Win+L", out var combination));

        Assert.Equal([0xFFE3UL, 0xFFE9UL, 0xFFE1UL, 0xFFEBUL, 0x6CUL], X11Keys.KeysymsOf(combination));
    }

    [Fact]
    public void Every_key_a_combination_can_name_has_an_x11_key()
    {
        string[] names =
        [
            "Backspace", "Tab", "Enter", "Pause", "Esc", "Space", "PageUp", "PageDown", "End", "Home", "Left", "Up", "Right",
            "Down", "PrintScreen", "Insert", "Delete", "VolumeMute", "VolumeDown", "VolumeUp", "MediaNext", "MediaPrevious",
            "MediaStop", "MediaPlayPause",
        ];
        var keys = names
            .Concat(Enumerable.Range('A', 26).Select(letter => ((char)letter).ToString()))
            .Concat(Enumerable.Range(0, 10).Select(digit => digit.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .Concat(Enumerable.Range(1, 24).Select(number => $"F{number}"));

        Assert.All(keys, name =>
        {
            Assert.True(KeyCombination.TryParse(name, out var combination), name);
            Assert.NotNull(X11Keys.KeysymOf(combination.VirtualKey));
        });
    }

    [Fact]
    public async Task The_service_has_keys_pressed_on_the_keyboard_of_the_session()
    {
        await using var bus = new ChannelEventBus();
        var keyboard = new FakeKeyboard();
        using var action = new KeyPressAction(bus, NullLogger<KeyPressAction>.Instance, keyboard);
        await action.StartAsync(CancellationToken.None);

        await bus.PublishAsync(new ActionCommand { ActionId = SessionCommands.PressKeys, Value = "no such keys" });
        await bus.PublishAsync(new ActionCommand { ActionId = SessionCommands.PressKeys, Value = "Ctrl+Alt+L" });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (keyboard.Pressed.IsEmpty)
        {
            await Task.Delay(20, timeout.Token);
        }

        Assert.Equal([new KeyCombination(KeyModifiers.Ctrl | KeyModifiers.Alt, 'L')], keyboard.Pressed);
        await action.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// An X server, as the build agents run one with Xvfb: its keyboard takes the keys, and the window in front
    /// can be asked about, though without a window manager there is none.
    /// </summary>
    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void Keys_are_pressed_on_an_x11_display()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
        {
            // Without a display there is nothing to press keys on; the build agents have one, and must run this.
            Assert.Null(Environment.GetEnvironmentVariable("CI"));
            return;
        }

        using var keyboard = new X11Keyboard();
        foreach (var keys in new[] { "Shift+A", "MediaPlayPause" })
        {
            Assert.True(KeyCombination.TryParse(keys, out var combination));
            Assert.Null(keyboard.Press(combination));
        }

        using var window = X11ActiveWindow.Open();
        Assert.NotNull(window);
        _ = window.Read();
    }

    private sealed class FakeKeyboard : IKeyboard
    {
        public ConcurrentQueue<KeyCombination> Pressed { get; } = new();

        public string? Press(KeyCombination combination)
        {
            Pressed.Enqueue(combination);
            return null;
        }
    }
}
