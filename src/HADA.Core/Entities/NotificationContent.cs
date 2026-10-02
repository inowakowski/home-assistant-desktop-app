using System.Text.Json;

namespace HADA.Core.Entities;

/// <summary>A button of a notification: what it says, and the id reported back when it is pressed.</summary>
public sealed record NotificationButton(string Action, string Title);

/// <summary>
/// What a notification may carry besides its text, in the form both engines hand it to the notification entity:
/// as <see cref="Models.ActionCommand.Parameters"/> named <c>title</c>, <c>image</c> and <c>actions</c>.
/// </summary>
public static class NotificationContent
{
    public const string Title = "title";

    /// <summary>An <c>http</c> or <c>https</c> address of a picture to show with the notification.</summary>
    public const string Image = "image";

    /// <summary>The buttons, as the JSON text of a list of objects with <c>action</c> and <c>title</c>.</summary>
    public const string Actions = "actions";

    /// <summary>Windows shows at most five buttons on a notification.</summary>
    public const int MaxButtons = 5;

    private const int MaxActionLength = 64;
    private const int MaxButtonTitleLength = 40;
    private const int MaxImageUrlLength = 2048;

    private static readonly System.Buffers.SearchValues<char> ActionCharacters =
        System.Buffers.SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-.");

    /// <summary>
    /// Reads <c>{"message": "…", "title": "…", "image": "…", "actions": [{"action": "…", "title": "…"}]}</c>.
    /// Returns <see langword="false"/> when there is no message. Anything else that is missing or malformed is
    /// left out rather than refused, so a notification is still shown.
    /// </summary>
    public static bool TryRead(JsonElement json, out string? message, out Dictionary<string, object?>? parameters)
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

        if (ReadString(json, Image) is { Length: > 0 and <= MaxImageUrlLength } image
            && Uri.TryCreate(image, UriKind.Absolute, out var address)
            && (address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps))
        {
            extras[Image] = address.AbsoluteUri;
        }

        if (json.TryGetProperty(Actions, out var actions) && ReadButtons(actions) is { Count: > 0 } buttons)
        {
            extras[Actions] = JsonSerializer.Serialize(buttons);
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
            return ReadButtons(json.RootElement);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static List<NotificationButton> ReadButtons(JsonElement actions)
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
                buttons.Add(new NotificationButton(action, title.Length > MaxButtonTitleLength ? title[..MaxButtonTitleLength] : title));
            }
        }

        return buttons;
    }

    private static string? ReadString(JsonElement json, string property) =>
        json.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;
}
