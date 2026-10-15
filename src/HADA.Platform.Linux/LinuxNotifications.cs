using System.Collections.Concurrent;
using HADA.Core;
using HADA.Core.Actions;
using HADA.Core.Entities;
using Microsoft.Extensions.Logging;
using Tmds.DBus.Protocol;

namespace HADA.Platform.Linux;

/// <summary>
/// Shows notifications through the desktop's notification server, <c>org.freedesktop.Notifications</c> on the
/// session's D-Bus, which GNOME, KDE and the other desktops have: with a picture and buttons where the server
/// shows them. A pressed button is reported back through <paramref name="actionPressed"/> with the action the
/// notification gave it, and the engine the notification came through, so that the answer goes to the Home
/// Assistant that asked.
/// </summary>
/// <param name="openAddress">Opens an <c>http</c> or <c>https</c> address in the browser.</param>
public sealed partial class LinuxNotifications(Action<string, string?> actionPressed, Action<string> openAddress, ILogger logger)
    : IAsyncDisposable
{
    private const string Service = "org.freedesktop.Notifications";
    private const string ObjectPath = "/org/freedesktop/Notifications";

    // Pressing the notification itself, as the specification names it.
    private const string DefaultAction = "default";

    /// <summary>
    /// Marks a button's key as an address to open rather than an action to report. An action has no colon in it,
    /// so the two cannot be mistaken.
    /// </summary>
    private const string AddressPrefix = "open:";

    // Urgency as the specification numbers it: normal, or critical, which stays on the screen.
    private const byte Normal = 1;
    private const byte Critical = 2;

    private readonly ConcurrentDictionary<uint, Shown> _shown = new();
    private readonly ConcurrentDictionary<string, uint> _tagged = new(StringComparer.Ordinal);
    private DBusConnection? _connection;

    /// <summary>
    /// Connects to the session's bus and listens for buttons pressed. Returns <see langword="false"/> where there is
    /// no notification server to talk to; notifications are then not shown.
    /// </summary>
    public async Task<bool> StartAsync()
    {
        if (DBusAddress.Session is not { } address)
        {
            LogUnavailable(logger, "there is no session bus.");
            return false;
        }

        var connection = new DBusConnection(address);
        try
        {
            await connection.ConnectAsync();
            await connection.AddMatchAsync(
                new MatchRule { Type = MessageType.Signal, Interface = Service, Member = "ActionInvoked" },
                static (message, _) =>
                {
                    var reader = message.GetBodyReader();
                    return (Id: reader.ReadUInt32(), Key: reader.ReadString());
                },
                pressed =>
                {
                    if (pressed.HasValue)
                    {
                        OnPressed(pressed.Value.Id, pressed.Value.Key);
                    }
                },
                emitOnCapturedContext: false,
                ObserverFlags.None);
            await connection.AddMatchAsync(
                new MatchRule { Type = MessageType.Signal, Interface = Service, Member = "NotificationClosed" },
                static (message, _) => message.GetBodyReader().ReadUInt32(),
                closed =>
                {
                    if (closed.HasValue)
                    {
                        _shown.TryRemove(closed.Value, out _);
                    }
                },
                emitOnCapturedContext: false,
                ObserverFlags.None);
            _connection = connection;
            return true;
        }
        catch (Exception ex) when (IsBusFailure(ex))
        {
            LogUnavailable(logger, ex.Message);
            connection.Dispose();
            return false;
        }
    }

    public async Task ShowAsync(NotificationRequest request)
    {
        if (_connection is not { } connection)
        {
            return;
        }

        var tag = TagOf(request);
        try
        {
            if (request.Clear)
            {
                if (tag is not null && _tagged.TryRemove(tag, out var shownId))
                {
                    await CloseAsync(connection, shownId);
                }

                return;
            }

            var imagePath = request.ImageUrl is null ? null : await NotificationImages.TryDownloadAsync(request.ImageUrl, reason => LogImageFailed(logger, reason));
            var replaces = tag is not null && _tagged.TryGetValue(tag, out var earlier) ? earlier : 0;
            var id = await NotifyAsync(connection, request, replaces, imagePath);
            _shown[id] = new Shown(request.Origin, NotificationContent.ToHttpUrl(request.Url));
            if (tag is not null)
            {
                _tagged[tag] = id;
            }
        }
        catch (Exception ex) when (IsBusFailure(ex))
        {
            LogNotShown(logger, ex.Message);
        }
    }

    public ValueTask DisposeAsync()
    {
        _connection?.Dispose();
        _connection = null;
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The notification's buttons as the server takes them: a key and a label each. A button with an address opens
    /// it instead of being reported; pressing the notification opens its own address, if it has one.
    /// </summary>
    public static string[] ActionsOf(NotificationRequest request)
    {
        var actions = new List<string>();
        if (NotificationContent.ToHttpUrl(request.Url) is not null)
        {
            actions.AddRange([DefaultAction, string.Empty]);
        }

        foreach (var button in request.Buttons)
        {
            actions.Add(NotificationContent.ToHttpUrl(button.Uri) is { } address ? AddressPrefix + address : button.Action);
            actions.Add(button.Title);
        }

        return [.. actions];
    }

    /// <summary>The text, with what servers that read markup would take for it escaped.</summary>
    public static string BodyOf(string message) =>
        message.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    private static async Task<uint> NotifyAsync(DBusConnection connection, NotificationRequest request, uint replaces, string? imagePath)
    {
        var hints = new Dictionary<string, VariantValue>
        {
            ["urgency"] = VariantValue.Byte(request.Sticky ? Critical : Normal),
            ["desktop-entry"] = VariantValue.String("hada"),
        };
        if (request.Silent)
        {
            hints["suppress-sound"] = VariantValue.Bool(true);
        }

        if (imagePath is not null)
        {
            hints["image-path"] = VariantValue.String(new Uri(imagePath).AbsoluteUri);
        }

        MessageBuffer message;
        using (var writer = connection.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(Service, ObjectPath, Service, "Notify", "susssasa{sv}i", MessageFlags.None);
            writer.WriteString("HADA");
            writer.WriteUInt32(replaces);
            writer.WriteString("hada");
            writer.WriteString(request.Title);
            writer.WriteString(BodyOf(request.Message));
            writer.WriteArray(ActionsOf(request));
            writer.WriteDictionary(hints);

            // A sticky one stays until it is dismissed; any other as long as the desktop keeps them.
            writer.WriteInt32(request.Sticky ? 0 : -1);
            message = writer.CreateMessage();
        }

        return await connection.CallMethodAsync(message, static (reply, _) => reply.GetBodyReader().ReadUInt32(), null);
    }

    private static async Task CloseAsync(DBusConnection connection, uint id)
    {
        MessageBuffer message;
        using (var writer = connection.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(Service, ObjectPath, Service, "CloseNotification", "u", MessageFlags.None);
            writer.WriteUInt32(id);
            message = writer.CreateMessage();
        }

        await connection.CallMethodAsync(message);
    }

    /// <summary>
    /// Tags are told apart by where they came from, so that two Home Assistants using the same tag do not replace
    /// or take back each other's notifications.
    /// </summary>
    private static string? TagOf(NotificationRequest request) =>
        request.Tag is { Length: > 0 } tag ? $"{request.Origin}\n{tag}" : null;

    private static bool IsBusFailure(Exception ex) =>
        ex is DBusConnectionException or DBusErrorReplyException or DBusMessageException or DBusReadException
            or IOException or ObjectDisposedException or InvalidOperationException;

    private void OnPressed(uint id, string key)
    {
        // Only this process's notifications; other apps' buttons are signalled on the same bus.
        if (!_shown.TryGetValue(id, out var shown))
        {
            return;
        }

        if (key == DefaultAction)
        {
            if (shown.Url is { } url)
            {
                openAddress(url);
            }
        }
        else if (key.StartsWith(AddressPrefix, StringComparison.Ordinal))
        {
            openAddress(key[AddressPrefix.Length..]);
        }
        else
        {
            actionPressed(key, shown.Origin);
        }
    }

    private sealed record Shown(string? Origin, string? Url);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notifications cannot be shown: {Reason}")]
    private static partial void LogUnavailable(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A notification could not be shown: {Reason}")]
    private static partial void LogNotShown(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "The picture of a notification could not be fetched: {Reason}")]
    private static partial void LogImageFailed(ILogger logger, string reason);
}
