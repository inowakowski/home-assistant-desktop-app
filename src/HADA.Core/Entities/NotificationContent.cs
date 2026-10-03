using System.Text.Json;

namespace HADA.Core.Entities;

/// <summary>A button of a notification: what it says, and the id reported back when it is pressed.</summary>
/// <param name="Uri">
/// An <c>http</c> or <c>https</c> address the button opens instead of being reported; <see langword="null"/> for
/// a button that is reported.
/// </param>
public sealed record NotificationButton(string Action, string Title, string? Uri = null);

/// <summary>
/// What a notification may carry besides its text, in the form both engines hand it to the notification entity:
/// as <see cref="Models.ActionCommand.Parameters"/> named <c>title</c>, <c>image</c>, <c>actions</c>, <c>tag</c>,
/// <c>url</c>, <c>sticky</c>, <c>silent</c> and <c>clear</c>.
/// </summary>
public static class NotificationContent
{
    public const string Title = "title";

    /// <summary>An <c>http</c> or <c>https</c> address of a picture to show with the notification.</summary>
    public const string Image = "image";

    /// <summary>The buttons, as the JSON text of a list of objects with <c>action</c>, <c>title</c> and optionally <c>uri</c>.</summary>
    public const string Actions = "actions";

    /// <summary>A notification replaces the one shown earlier with the same tag, and can be taken back by it.</summary>
    public const string Tag = "tag";

    /// <summary>An <c>http</c> or <c>https</c> address opened when the notification itself is pressed.</summary>
    public const string Url = "url";

    /// <summary><see cref="True"/> for a notification that stays on the screen until it is dismissed.</summary>
    public const string Sticky = "sticky";

    /// <summary><see cref="True"/> for a notification that makes no sound.</summary>
    public const string Silent = "silent";

    /// <summary><see cref="True"/> when nothing is to be shown: the notification with this <see cref="Tag"/> is taken back.</summary>
    public const string Clear = "clear";

    /// <summary>The value of the parameters that are either there or not.</summary>
    public const string True = "true";

    /// <summary>The message that, with a <c>tag</c>, takes a notification back; Home Assistant's companion apps use the same.</summary>
    public const string ClearMessage = "clear_notification";

    /// <summary>Windows shows at most five buttons on a notification.</summary>
    public const int MaxButtons = 5;

    /// <summary>As long as Windows lets the tag of a notification be.</summary>
    public const int MaxTagLength = 64;

    private const int MaxActionLength = 64;
    private const int MaxButtonTitleLength = 40;
    private const int MaxUrlLength = 2048;

    private static readonly System.Buffers.SearchValues<char> ActionCharacters =
        System.Buffers.SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-.");

    /// <summary>
    /// Reads <c>{"message": "…", "title": "…", "image": "…", "actions": [{"action": "…", "title": "…"}],
    /// "tag": "…", "url": "…", "sticky": true, "silent": true}</c>.
    /// Returns <see langword="false"/> when there is no message. Anything else that is missing or malformed is
    /// left out rather than refused, so a notification is still shown.
    /// </summary>
    /// <param name="baseUrl">
    /// The Home Assistant the notification came from, when known. An address that is only a path, such as
    /// <c>/local/door.jpg</c>, is one of its own.
    /// </param>
    public static bool TryRead(JsonElement json, out string? message, out Dictionary<string, object?>? parameters, Uri? baseUrl = null) =>
        TryRead(json, json, baseUrl, out message, out parameters);

    /// <summary>
    /// Reads a notification as Home Assistant's <c>notify.mobile_app_*</c> actions send it to a phone: the message
    /// and the title as above, and everything else inside <c>data</c>, where <c>clickAction</c> also stands for
    /// <c>url</c> and <c>persistent</c> for <c>sticky</c>.
    /// </summary>
    public static bool TryReadMobileApp(JsonElement json, Uri? baseUrl, out string? message, out Dictionary<string, object?>? parameters) =>
        TryRead(
            json,
            json.ValueKind == JsonValueKind.Object && json.TryGetProperty("data", out var data) ? data : default,
            baseUrl,
            out message,
            out parameters);

    /// <param name="details">Where everything but the message and the title is.</param>
    private static bool TryRead(
        JsonElement json, JsonElement details, Uri? baseUrl, out string? message, out Dictionary<string, object?>? parameters)
    {
        message = null;
        parameters = null;
        if (json.ValueKind != JsonValueKind.Object
            || !json.TryGetProperty("message", out var text)
            || text.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        message = text.GetString();
        var extras = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (ReadString(json, Title) is { Length: > 0 } title)
        {
            extras[Title] = title;
        }

        if (details.ValueKind == JsonValueKind.Object)
        {
            if (ReadString(details, Image) is { } image && ToHttpUrl(image, baseUrl) is { } imageUrl)
            {
                extras[Image] = imageUrl;
            }

            if (details.TryGetProperty(Actions, out var actions) && ReadButtons(actions, baseUrl) is { Count: > 0 } buttons)
            {
                extras[Actions] = JsonSerializer.Serialize(buttons);
            }

            if (ReadString(details, Tag) is { Length: > 0 } tag)
            {
                extras[Tag] = tag.Length > MaxTagLength ? tag[..MaxTagLength] : tag;

                // Only with a tag is there something to take back; without one the text is shown, whatever it says.
                if (message?.Trim() == ClearMessage)
                {
                    extras[Clear] = True;
                }
            }

            if ((ReadString(details, Url) ?? ReadString(details, "clickAction")) is { } url && ToHttpUrl(url, baseUrl) is { } clickUrl)
            {
                extras[Url] = clickUrl;
            }

            if (IsTrue(details, Sticky) || IsTrue(details, "persistent"))
            {
                extras[Sticky] = True;
            }

            if (IsSilent(details))
            {
                extras[Silent] = True;
            }
        }

        parameters = extras.Count > 0 ? extras : null;
        return true;
    }

    /// <summary>Reads the buttons back from the <see cref="Actions"/> parameter; empty when there are none.</summary>
    public static IReadOnlyList<NotificationButton> ParseButtons(string? actionsJson)
    {
        if (string.IsNullOrEmpty(actionsJson))
        {
            return [];
        }

        try
        {
            using var json = JsonDocument.Parse(actionsJson);
            return ReadButtons(json.RootElement, baseUrl: null);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// The address if it is one a notification may point at: <c>http</c> or <c>https</c>, or a path at
    /// <paramref name="baseUrl"/>. <see langword="null"/> otherwise.
    /// </summary>
    public static string? ToHttpUrl(string? address, Uri? baseUrl = null)
    {
        if (address is not { Length: > 0 and <= MaxUrlLength })
        {
            return null;
        }

        // "//host/path" would be another computer's, whatever the base; only a plain path is Home Assistant's own.
        var url = baseUrl is not null && address.StartsWith('/') && !address.StartsWith("//", StringComparison.Ordinal)
            ? new Uri(baseUrl, address.TrimStart('/'))
            : Uri.TryCreate(address, UriKind.Absolute, out var absolute) ? absolute : null;
        return url is not null && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps)
            ? url.AbsoluteUri
            : null;
    }

    private static List<NotificationButton> ReadButtons(JsonElement actions, Uri? baseUrl)
    {
        var buttons = new List<NotificationButton>();
        if (actions.ValueKind != JsonValueKind.Array)
        {
            return buttons;
        }

        foreach (var item in actions.EnumerateArray())
        {
            if (buttons.Count == MaxButtons)
            {
                break;
            }

            // The action ends up in an MQTT payload and in a notification's markup, so it is an id, not free text.
            if (item.ValueKind == JsonValueKind.Object
                && (ReadString(item, "action") ?? ReadString(item, "Action")) is { Length: > 0 and <= MaxActionLength } action
                && !action.AsSpan().ContainsAnyExcept(ActionCharacters)
                && (ReadString(item, "title") ?? ReadString(item, "Title")) is { Length: > 0 } title)
            {
                buttons.Add(new NotificationButton(
                    action,
                    title.Length > MaxButtonTitleLength ? title[..MaxButtonTitleLength] : title,
                    ToHttpUrl(ReadString(item, "uri") ?? ReadString(item, "Uri"), baseUrl)));
            }
        }

        return buttons;
    }

    /// <summary>
    /// A phone is told to keep quiet in several ways; any of <c>silent: true</c>, <c>importance: low</c> or
    /// <c>min</c>, and <c>push: {sound: none}</c> will do.
    /// </summary>
    private static bool IsSilent(JsonElement details) =>
        IsTrue(details, Silent)
        || ReadString(details, "importance") is "low" or "min"
        || (details.TryGetProperty("push", out var push) && push.ValueKind == JsonValueKind.Object && ReadString(push, "sound") == "none");

    /// <summary>YAML written by hand makes <c>true</c> a text as easily as a truth value.</summary>
    private static bool IsTrue(JsonElement json, string property) =>
        json.TryGetProperty(property, out var value)
        && (value.ValueKind == JsonValueKind.True
            || (value.ValueKind == JsonValueKind.String && string.Equals(value.GetString()?.Trim(), True, StringComparison.OrdinalIgnoreCase)));

    private static string? ReadString(JsonElement json, string property) =>
        json.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;
}
