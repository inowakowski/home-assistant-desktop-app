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
}
