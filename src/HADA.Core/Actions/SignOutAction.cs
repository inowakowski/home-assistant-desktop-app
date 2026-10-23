using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Models;
using Microsoft.Extensions.Logging;

namespace HADA.Core.Actions;

/// <summary>
/// A "Sign out" button for the user at the computer, as the desktop's own menu has. Off until switched on in
/// settings, like the power buttons: whoever can press it in Home Assistant can end the user's session. Runs in the
/// user's session; left out where the desktop cannot be told to sign out.
/// </summary>
public sealed partial class SignOutAction(IEventBus bus, IEntityRegistry registry, ILogger<SignOutAction> logger, ISignOut signOut)
    : CommandHandler(bus, registry, logger)
{
    protected override IReadOnlyList<EntityDescriptor> Entities { get; } = signOut.CanSignOut
        ? [new() { Id = BuiltInEntityIds.SignOut, Name = "Sign out", Kind = EntityKind.Button, Icon = "mdi:logout", EnabledByDefault = false }]
        : [];

    protected override ValueTask HandleAsync(ActionCommand command, CancellationToken cancellationToken)
    {
        if (signOut.SignOut() is { } reason)
        {
            LogFailed(Logger, reason);
        }
        else
        {
            LogSignedOut(Logger, command.Origin);
        }

        return ValueTask.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Signing out (requested via {Origin}).")]
    private static partial void LogSignedOut(ILogger logger, string? origin);

    [LoggerMessage(Level = LogLevel.Error, Message = "Signing out failed: {Reason}")]
    private static partial void LogFailed(ILogger logger, string reason);
}
