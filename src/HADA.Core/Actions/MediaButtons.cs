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
    private static readonly (EntityDescriptor Button, string Action)[] Buttons =
    [
        (BuiltInDescriptors.MediaPlayPause, MediaCommand.PlayPause),
        (BuiltInDescriptors.MediaNext, MediaCommand.Next),
        (BuiltInDescriptors.MediaPrevious, MediaCommand.Previous),
        (BuiltInDescriptors.MediaStop, MediaCommand.Stop),
    ];

    protected override IReadOnlyList<EntityDescriptor> Entities { get; } =
        [.. Buttons.Where(button => media.Supports(button.Action)).Select(button => button.Button)];

    protected override async ValueTask HandleAsync(ActionCommand command, CancellationToken cancellationToken)
    {
        var action = Array.Find(Buttons, button => button.Button.Id == command.ActionId).Action ?? MediaCommand.Stop;

        if (!await media.ControlAsync(action, cancellationToken))
        {
            LogNoPlayer(Logger, action);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "No media player took '{Action}'.")]
    private static partial void LogNoPlayer(ILogger logger, string action);
}
