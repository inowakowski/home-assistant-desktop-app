using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Models;
using HADA.Platform.Windows.Actions;
using Microsoft.Extensions.Logging;

namespace HADA.Tray.Session;

/// <summary>A notification to show: its texts, and optionally a picture and buttons.</summary>
/// <param name="ImageUrl">An <c>http</c> or <c>https</c> address, or <see langword="null"/>.</param>
/// <param name="Origin">The engine the notification came through, which its buttons answer to.</param>
/// <param name="Tag">Replaces the notification shown earlier with the same tag from the same origin.</param>
/// <param name="Url">An <c>http</c> or <c>https</c> address opened when the notification itself is pressed.</param>
/// <param name="Sticky">Stays on the screen until it is dismissed.</param>
/// <param name="Silent">Makes no sound.</param>
/// <param name="Clear">Shows nothing: takes back the notification with this <paramref name="Tag"/>.</param>
public sealed record NotificationRequest(
    string Title,
    string Message,
    string? ImageUrl,
    IReadOnlyList<NotificationButton> Buttons,
    string? Origin = null,
    string? Tag = null,
    string? Url = null,
    bool Sticky = false,
    bool Silent = false,
    bool Clear = false);

/// <summary>Where notifications from Home Assistant are shown; the tray plugs itself in once it is ready.</summary>
public sealed class NotificationPresenter
{
    /// <summary>Shows a notification. May be called from any thread.</summary>
    public Action<NotificationRequest>? Show { get; set; }
}

/// <summary>
/// Exposes a notification entity: a message Home Assistant sends to it is shown to the signed-in user as a Windows
/// notification. Lives in the tray app, which notifications come from.
/// </summary>
public sealed partial class NotificationAction(
    IEventBus bus, IEntityRegistry registry, ILogger<NotificationAction> logger, NotificationPresenter presenter)
    : CommandHandler(bus, registry, logger)
{
    public const string EntityId = BuiltInEntityIds.Notification;

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
            show(new NotificationRequest(
                command.GetParameter(NotificationContent.Title) is { Length: > 0 } title ? title : DefaultTitle,
                command.Value,
                command.GetParameter(NotificationContent.Image),
                NotificationContent.ParseButtons(command.GetParameter(NotificationContent.Actions)),
                command.Origin,
                command.GetParameter(NotificationContent.Tag) is { Length: > 0 } tag ? tag : null,
                command.GetParameter(NotificationContent.Url),
                Sticky: command.GetParameter(NotificationContent.Sticky) == NotificationContent.True,
                Silent: command.GetParameter(NotificationContent.Silent) == NotificationContent.True,
                Clear: command.GetParameter(NotificationContent.Clear) == NotificationContent.True));

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
