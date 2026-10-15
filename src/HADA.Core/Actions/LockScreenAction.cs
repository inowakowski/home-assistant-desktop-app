using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Models;
using Microsoft.Extensions.Logging;

namespace HADA.Core.Actions;

/// <summary>Exposes a "Lock screen" button and locks the session at the computer's own screen when it is pressed.</summary>
public sealed partial class LockScreenAction(IEventBus bus, IEntityRegistry registry, ILogger<LockScreenAction> logger, IScreenLock screenLock)
    : CommandHandler(bus, registry, logger)
{
    public const string EntityId = BuiltInEntityIds.LockScreen;

    protected override IReadOnlyList<EntityDescriptor> Entities { get; } =
    [
        new EntityDescriptor
        {
            Id = EntityId,
            Name = "Lock screen",
            Kind = EntityKind.Button,
            Icon = "mdi:monitor-lock",
        },
    ];

    protected override ValueTask HandleAsync(ActionCommand command, CancellationToken cancellationToken)
    {
        if (screenLock.Lock() is { } error)
        {
            LogLockFailed(Logger, error);
        }
        else
        {
            LogLocked(Logger, command.Origin);
        }

        return ValueTask.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Screen locked (requested via {Origin}).")]
    private static partial void LogLocked(ILogger logger, string? origin);

    [LoggerMessage(Level = LogLevel.Error, Message = "Locking the screen failed: {Reason}")]
    private static partial void LogLockFailed(ILogger logger, string reason);
}
