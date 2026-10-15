using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Models;
using Microsoft.Extensions.Logging;

namespace HADA.Core.Actions;

/// <summary>
/// Exposes "Sleep", "Hibernate", "Shut down" and "Restart" buttons. Runs in the service, which may do all four
/// whether or not anybody is signed in; how each is done is the system's, through <see cref="IPowerControl"/>.
/// </summary>
/// <remarks>
/// The buttons are off until switched on in settings (<see cref="EntityDescriptor.EnabledByDefault"/>): whoever can
/// press them in Home Assistant can switch this computer off.
/// </remarks>
public sealed partial class PowerActions(IEventBus bus, IEntityRegistry registry, ILogger<PowerActions> logger, IPowerControl power)
    : CommandHandler(bus, registry, logger)
{
    public const string SleepEntityId = BuiltInEntityIds.Sleep;
    public const string HibernateEntityId = BuiltInEntityIds.Hibernate;
    public const string ShutdownEntityId = BuiltInEntityIds.Shutdown;
    public const string RestartEntityId = BuiltInEntityIds.Restart;

    protected override IReadOnlyList<EntityDescriptor> Entities { get; } =
    [
        Button(SleepEntityId, "Sleep", "mdi:power-sleep"),
        Button(HibernateEntityId, "Hibernate", "mdi:power-sleep"),
        Button(ShutdownEntityId, "Shut down", "mdi:power"),
        Button(RestartEntityId, "Restart", "mdi:restart"),
    ];

    protected override ValueTask HandleAsync(ActionCommand command, CancellationToken cancellationToken)
    {
        var request = command.ActionId switch
        {
            SleepEntityId => PowerCommand.Sleep,
            HibernateEntityId => PowerCommand.Hibernate,
            ShutdownEntityId => PowerCommand.Shutdown,
            _ => PowerCommand.Restart,
        };

        LogRequested(Logger, request, command.Origin);

        // On its own thread: putting the computer to sleep returns only after it woke up again.
        _ = Task.Run(
            () =>
            {
                if (power.Execute(request) is { } error)
                {
                    LogFailed(Logger, request, error);
                }
            },
            CancellationToken.None);
        return ValueTask.CompletedTask;
    }

    private static EntityDescriptor Button(string id, string name, string icon) =>
        new() { Id = id, Name = name, Kind = EntityKind.Button, Icon = icon, EnabledByDefault = false };

    [LoggerMessage(Level = LogLevel.Information, Message = "{Command} requested via {Origin}.")]
    private static partial void LogRequested(ILogger logger, PowerCommand command, string? origin);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Command} failed: {Reason}")]
    private static partial void LogFailed(ILogger logger, PowerCommand command, string reason);
}
