using System.Runtime.InteropServices;
using HADA.Core.Abstractions;
using HADA.Core.Actions;
using HADA.Core.Entities;
using HADA.Core.Hosting;
using HADA.Core.Input;
using HADA.Core.Models;
using HADA.Platform.Windows.Interop;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HADA.Platform.Windows.Actions;

/// <summary>
/// Exposes "Turn off display" and "Wake display" buttons. Runs in the tray app: both act on the desktop of the
/// signed-in user, which a service cannot reach.
/// </summary>
public sealed partial class DisplayActions(IEventBus bus, IEntityRegistry registry, ILogger<DisplayActions> logger)
    : CommandHandler(bus, registry, logger)
{
    public const string TurnOffEntityId = BuiltInEntityIds.TurnOffDisplay;
    public const string WakeEntityId = BuiltInEntityIds.WakeDisplay;

    private const uint SystemCommandMessage = 0x0112;
    private const nint MonitorPowerCommand = 0xF170;
    private const nint MonitorOff = 2;
    private const nint Broadcast = 0xFFFF;

    protected override IReadOnlyList<EntityDescriptor> Entities { get; } =
    [
        new() { Id = TurnOffEntityId, Name = "Turn off display", Kind = EntityKind.Button, Icon = "mdi:monitor-off" },
        new() { Id = WakeEntityId, Name = "Wake display", Kind = EntityKind.Button, Icon = "mdi:monitor-shimmer" },
    ];

    protected override ValueTask HandleAsync(ActionCommand command, CancellationToken cancellationToken)
    {
        var done = command.ActionId == TurnOffEntityId ? TurnOff() : InputSimulator.NudgeMouse();
        if (done)
        {
            LogDone(Logger, command.ActionId, command.Origin);
        }
        else
        {
            LogFailed(Logger, command.ActionId, Marshal.GetLastPInvokeError());
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The monitor-power system command, handled by any window's default processing. It is posted to the desktop's
    /// own window, and not sent, so a window that hangs cannot hold it up.
    /// </summary>
    private static bool TurnOff()
    {
        var shell = NativeMethods.GetShellWindow();
        return NativeMethods.PostMessage(shell != 0 ? shell : Broadcast, SystemCommandMessage, MonitorPowerCommand, MonitorOff);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "'{EntityId}' done (requested via {Origin}).")]
    private static partial void LogDone(ILogger logger, string entityId, string? origin);

    [LoggerMessage(Level = LogLevel.Error, Message = "'{EntityId}' failed with error {Error}.")]
    private static partial void LogFailed(ILogger logger, string entityId, int error);
}

/// <summary>
/// Exposes play/pause, next, previous and stop buttons, which press the multimedia keys. Runs in the tray app:
/// the keys go to whichever app in the user's session handles them.
/// </summary>
public sealed partial class MediaKeyActions(IEventBus bus, IEntityRegistry registry, ILogger<MediaKeyActions> logger)
    : CommandHandler(bus, registry, logger)
{
    public const string PlayPauseEntityId = BuiltInEntityIds.MediaPlayPause;
    public const string NextEntityId = BuiltInEntityIds.MediaNext;
    public const string PreviousEntityId = BuiltInEntityIds.MediaPrevious;
    public const string StopEntityId = BuiltInEntityIds.MediaStop;

    protected override IReadOnlyList<EntityDescriptor> Entities { get; } =
    [
        BuiltInDescriptors.MediaPlayPause,
        BuiltInDescriptors.MediaNext,
        BuiltInDescriptors.MediaPrevious,
        BuiltInDescriptors.MediaStop,
    ];

    protected override ValueTask HandleAsync(ActionCommand command, CancellationToken cancellationToken)
    {
        var key = command.ActionId switch
        {
            PlayPauseEntityId => MediaKey.PlayPause,
            NextEntityId => MediaKey.NextTrack,
            PreviousEntityId => MediaKey.PreviousTrack,
            _ => MediaKey.Stop,
        };

        if (!InputSimulator.Press(key))
        {
            LogFailed(Logger, key, Marshal.GetLastPInvokeError());
        }

        return ValueTask.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Pressing the {Key} key failed with error {Error}.")]
    private static partial void LogFailed(ILogger logger, MediaKey key, int error);
}

/// <summary>The keyboard of the user's desktop, through <c>SendInput</c>.</summary>
public sealed class WindowsKeyboard : IKeyboard
{
    public string? Press(KeyCombination combination) =>
        InputSimulator.Press(combination) ? null : $"error {Marshal.GetLastPInvokeError()}";
}
