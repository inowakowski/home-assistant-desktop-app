using System.Text.Json;
using HADA.Core.Entities;
using HADA.Core.Input;
using HADA.Core.Models;
using HADA.Platform.Windows.Actions;

namespace HADA.Tests.Entities;

/// <summary>What travels from the computer to Home Assistant on the user's initiative, and what a notification may carry.</summary>
public class EventTests
{
    [Theory]
    [InlineData(DeviceEvent.QuickAction, "toggle_lamp", true)]
    [InlineData(DeviceEvent.NotificationAction, "open-door.1", true)]
    [InlineData(DeviceEvent.NotificationAction, "", false)]
    [InlineData(DeviceEvent.NotificationAction, "with space", false)]
    [InlineData(DeviceEvent.NotificationAction, "topic/injection", false)]
    [InlineData(DeviceEvent.NotificationAction, "wild#card", false)]
    [InlineData("availability", "offline", false)]
    [InlineData("../other", "x", false)]
    public void Only_known_events_with_id_like_values_are_passed_on(string name, string value, bool expected)
    {
        Assert.Equal(expected, new DeviceEvent { Name = name, Value = value }.IsWellFormed);
    }

    [Fact]
    public void An_event_value_longer_than_an_id_is_refused()
    {
        Assert.True(new DeviceEvent { Name = DeviceEvent.NotificationAction, Value = new string('a', 64) }.IsWellFormed);
        Assert.False(new DeviceEvent { Name = DeviceEvent.NotificationAction, Value = new string('a', 65) }.IsWellFormed);
    }

    [Fact]
    public void A_notification_carries_title_picture_and_buttons()
    {
        using var json = JsonDocument.Parse("""
            {
              "title": "Front door",
              "message": "Someone is at the door.",
              "image": "http://homeassistant.local:8123/local/door.jpg",
              "actions": [
                { "action": "open_door", "title": "Open" },
                { "action": "ignore", "title": "Ignore" }
              ]
            }
            """);

        Assert.True(NotificationContent.TryRead(json.RootElement, out var message, out var parameters));

        Assert.Equal("Someone is at the door.", message);
        Assert.Equal("Front door", parameters![NotificationContent.Title]);
        Assert.Equal("http://homeassistant.local:8123/local/door.jpg", parameters[NotificationContent.Image]);
        Assert.Equal(
            [new NotificationButton("open_door", "Open"), new NotificationButton("ignore", "Ignore")],
            NotificationContent.ParseButtons((string)parameters[NotificationContent.Actions]!));
    }

    [Fact]
    public void What_is_wrong_with_a_notification_is_left_out_and_the_message_still_shown()
    {
        using var json = JsonDocument.Parse("""
            {
              "message": "Hello",
              "title": 5,
              "image": "file:///C:/Windows/win.ini",
              "actions": [
                { "action": "has space", "title": "No" },
                { "action": "no_title" },
                "not an object",
                { "action": "a", "title": "1" }, { "action": "b", "title": "2" }, { "action": "c", "title": "3" },
                { "action": "d", "title": "4" }, { "action": "e", "title": "5" }, { "action": "f", "title": "6" }
              ]
            }
            """);

        Assert.True(NotificationContent.TryRead(json.RootElement, out var message, out var parameters));

        Assert.Equal("Hello", message);
        Assert.False(parameters!.ContainsKey(NotificationContent.Title));

        // Only web addresses are fetched; a file on this computer is none of Home Assistant's business.
        Assert.False(parameters.ContainsKey(NotificationContent.Image));

        // Windows shows five buttons at most.
        Assert.Equal(
            ["a", "b", "c", "d", "e"],
            NotificationContent.ParseButtons((string)parameters[NotificationContent.Actions]!).Select(button => button.Action));
    }

    [Theory]
    [InlineData("""{ "title": "No message" }""")]
    [InlineData("""{ "message": 5 }""")]
    [InlineData("""[ "message" ]""")]
    public void Without_a_message_there_is_no_notification(string text)
    {
        using var json = JsonDocument.Parse(text);

        Assert.False(NotificationContent.TryRead(json.RootElement, out _, out _));
    }

    [Fact]
    public void Buttons_that_are_not_a_list_are_no_buttons()
    {
        Assert.Empty(NotificationContent.ParseButtons(null));
        Assert.Empty(NotificationContent.ParseButtons("not json"));
        Assert.Empty(NotificationContent.ParseButtons("""{ "action": "a", "title": "A" }"""));
    }

    [Fact]
    public void Quick_actions_survive_the_trip_to_the_tray()
    {
        QuickActionInfo[] actions = [new("toggle_lamp", "Lampa – przełącz", "Ctrl+Alt+L"), new("scene_movie", "Movie", string.Empty)];

        Assert.Equal(actions, QuickActionInfo.Deserialize(QuickActionInfo.Serialize(actions)));
        Assert.Empty(QuickActionInfo.Deserialize("not json"));
        Assert.Empty(QuickActionInfo.Deserialize(null));
        Assert.Equal(
            [new QuickActionInfo("a", "A", string.Empty)],
            QuickActionInfo.Deserialize("""[ { "id": "a", "name": "A" }, { "id": "", "name": "No id" }, { "id": "b" } ]"""));
    }

    [Theory]
    [InlineData("Ctrl+Alt+L", KeyModifiers.Ctrl | KeyModifiers.Alt, 'L', "Ctrl+Alt+L")]
    [InlineData("ctrl + shift + m", KeyModifiers.Ctrl | KeyModifiers.Shift, 'M', "Ctrl+Shift+M")]
    [InlineData("Win+Shift+S", KeyModifiers.Win | KeyModifiers.Shift, 'S', "Shift+Win+S")]
    [InlineData("F11", KeyModifiers.None, 0x7A, "F11")]
    [InlineData("Alt+F4", KeyModifiers.Alt, 0x73, "Alt+F4")]
    [InlineData("Control+5", KeyModifiers.Ctrl, '5', "Ctrl+5")]
    [InlineData("MediaNext", KeyModifiers.None, 0xB0, "MediaNext")]
    [InlineData("Ctrl+Escape", KeyModifiers.Ctrl, 0x1B, "Ctrl+Esc")]
    [InlineData("Win", KeyModifiers.Win, 0, "Win")]
    public void Key_combinations_are_read_as_written_in_settings(string text, KeyModifiers modifiers, int key, string canonical)
    {
        Assert.True(KeyCombination.TryParse(text, out var combination));
        Assert.Equal(new KeyCombination(modifiers, (ushort)key), combination);
        Assert.Equal(canonical, combination.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Ctrl+A")]
    [InlineData("A+B")]
    [InlineData("Ctrl+Banana")]
    [InlineData("F25")]
    [InlineData("F0")]
    [InlineData("Ctrl+ł")]
    public void What_is_not_a_key_combination_is_refused(string text)
    {
        Assert.False(KeyCombination.TryParse(text, out _));
    }
}
