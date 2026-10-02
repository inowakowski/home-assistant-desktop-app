using System.Text.Json;

namespace HADA.Core.Models;

/// <summary>
/// A quick action as the tray app needs to know it: what to show in the menu, which shortcut to listen for, and
/// the id to report when it is chosen.
/// </summary>
/// <param name="Hotkey">E.g. <c>Ctrl+Alt+L</c>; empty when the action is in the menu only.</param>
public sealed record QuickActionInfo(string Id, string Name, string Hotkey)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The form <see cref="SessionCommands.QuickActions"/> carries the list in.</summary>
    public static string Serialize(IReadOnlyList<QuickActionInfo> actions) => JsonSerializer.Serialize(actions, JsonOptions);

    /// <summary>Reads the list back; empty when the text is not such a list.</summary>
    public static IReadOnlyList<QuickActionInfo> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<QuickActionInfo[]>(json, JsonOptions)
                ?.Where(action => action is { Id.Length: > 0, Name.Length: > 0 })
                .Select(action => action with { Hotkey = action.Hotkey ?? string.Empty })
                .ToArray() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
