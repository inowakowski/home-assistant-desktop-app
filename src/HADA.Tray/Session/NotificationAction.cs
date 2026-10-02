using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Models;
using HADA.Platform.Windows.Actions;
using Microsoft.Extensions.Logging;

namespace HADA.Tray.Session;

/// <summary>Where notifications from Home Assistant are shown; the tray icon plugs itself in once it exists.</summary>
public sealed class NotificationPresenter
{
    /// <summary>Shows a notification with a title and a message. May be called from any thread.</summary>
    public Action<string, string>? Show { get; set; }
}

/// <summary>
/// Exposes a notification entity: a message Home Assistant sends to it is shown to the signed-in user as a Windows
/// notification. Lives in the tray app, which owns the notification-area icon the notification comes from.
/// </summary>
public sealed partial class NotificationAction(
    IEventBus bus, IEntityRegistry registry, ILogger<NotificationAction> logger, NotificationPresenter presenter)
    : CommandHandler(bus, registry, logger)
{
    public const string EntityId = "notification";

    private const string DefaultTitle = "Home Assistant";

    protected override IReadOnlyList<EntityDescriptor> Entities { get; } =
    [
        new() { Id = EntityId, Name = "Notification", Kind = EntityKind.Notify, Icon = "mdi:message-badge" },
    ];

    protected override ValueTask HandleAsync(ActionCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Value))
        {
            return ValueTask.CompletedTask;
        }

        if (presenter.Show is { } show)
        {
            show(command.GetParameter("title") is { Length: > 0 } title ? title : DefaultTitle, command.Value);

            // Not the text: what Home Assistant tells the user is none of the log's business.
            LogShown(Logger, command.Origin);
        }
        else
        {
            LogNotReady(Logger);
        }

        return ValueTask.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Showed a notification received via {Origin}.")]
    private static partial void LogShown(ILogger logger, string? origin);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A notification arrived before the tray icon was ready and was not shown.")]
    private static partial void LogNotReady(ILogger logger);
}
