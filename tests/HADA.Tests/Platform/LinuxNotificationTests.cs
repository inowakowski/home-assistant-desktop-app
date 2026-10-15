using HADA.Core.Actions;
using HADA.Core.Entities;
using HADA.Platform.Linux;

namespace HADA.Tests.Platform;

/// <summary>How a notification from Home Assistant is put to the Linux desktop's notification server.</summary>
public sealed class LinuxNotificationTests
{
    [Fact]
    public void Buttons_go_as_key_and_label_and_one_with_an_address_opens_it()
    {
        var request = new NotificationRequest(
            "Door",
            "Someone is at the door",
            ImageUrl: null,
            [new NotificationButton("OPEN_DOOR", "Open"), new NotificationButton("camera", "Camera", "https://ha.local/camera")]);

        Assert.Equal(["OPEN_DOOR", "Open", "open:https://ha.local/camera", "Camera"], LinuxNotifications.ActionsOf(request));
    }

    [Fact]
    public void A_notification_with_an_address_opens_it_when_pressed()
    {
        var request = new NotificationRequest("Door", "Someone is at the door", ImageUrl: null, [], Url: "https://ha.local/lovelace/door");

        Assert.Equal(["default", string.Empty], LinuxNotifications.ActionsOf(request));
    }

    [Fact]
    public void An_address_that_would_start_something_else_is_left_out()
    {
        var request = new NotificationRequest(
            "Door", "Text", ImageUrl: null, [new NotificationButton("x", "Run", "file:///usr/bin/rm")], Url: "javascript:alert(1)");

        Assert.Equal(["x", "Run"], LinuxNotifications.ActionsOf(request));
    }

    [Fact]
    public void Text_cannot_pass_for_markup() =>
        Assert.Equal("1 &lt; 2 &amp;&amp; &lt;b&gt; is bold", LinuxNotifications.BodyOf("1 < 2 && <b> is bold"));
}
