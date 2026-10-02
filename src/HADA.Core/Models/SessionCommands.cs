namespace HADA.Core.Models;

/// <summary>
/// Commands the service sends to the tray app on its own account, not for an entity. Their ids contain a dot,
/// which an entity id cannot, so Home Assistant can never address them.
/// </summary>
public static class SessionCommands
{
    public const string Prefix = "session.";

    /// <summary>Start the program, document or address in <see cref="ActionCommand.Value"/> as the signed-in user.</summary>
    public const string Launch = Prefix + "launch";

    /// <summary>Press the key combination in <see cref="ActionCommand.Value"/>, e.g. <c>Ctrl+Shift+M</c>, on the user's desktop.</summary>
    public const string PressKeys = Prefix + "keys";

    /// <summary>
    /// The quick actions to offer in the tray menu, as JSON in <see cref="ActionCommand.Value"/>: a list of objects
    /// with <c>id</c>, <c>name</c> and <c>hotkey</c>. Unlike the others this is not something to do once but something
    /// to know, so the service repeats the latest one to every tray app that connects.
    /// </summary>
    public const string QuickActions = Prefix + "quick_actions";

    /// <summary>Whether a command describes a state every tray app should be told on connecting, not an action.</summary>
    public static bool IsSticky(string actionId) => actionId == QuickActions;
}
