using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Models;
using Microsoft.Extensions.Logging;

namespace HADA.Core.Actions;

/// <summary>
/// Play/pause, next, previous and stop buttons, which tell the player the system's own media controls show.
/// Runs in the user's session. Windows has its own: there the buttons press the multimedia keys, which also reach
/// apps that offer no media controls.
/// </summary>
public sealed partial class MediaButtons(IEventBus bus, IEntityRegistry registry, ILogger<MediaButtons> logger, IMediaSessions media)
    : CommandHandler(bus, registry, logger)
{
    protected override IReadOnlyList<EntityDescriptor> Entities { get; } =
    [
        BuiltInDescriptors.MediaPlayPause,
        BuiltInDescriptors.MediaNext,
        BuiltInDescriptors.MediaPrevious,
        BuiltInDescriptors.MediaStop,
    ];

    protected override async ValueTask HandleAsync(ActionCommand command, CancellationToken cancellationToken)
    {
        var action = command.ActionId switch
        {
            BuiltInEntityIds.MediaPlayPause => MediaCommand.PlayPause,
            BuiltInEntityIds.MediaNext => MediaCommand.Next,
            BuiltInEntityIds.MediaPrevious => MediaCommand.Previous,
            _ => MediaCommand.Stop,
        };

        if (!await media.ControlAsync(action, cancellationToken))
        {
            LogNoPlayer(Logger, action);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "No media player took '{Action}'.")]
    private static partial void LogNoPlayer(ILogger logger, string action);
}
