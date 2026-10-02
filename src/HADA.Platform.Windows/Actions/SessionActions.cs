using System.Diagnostics;
using System.Runtime.InteropServices;
using HADA.Core.Abstractions;
using HADA.Core.Entities;
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
    public const string TurnOffEntityId = "turn_off_display";
    public const string WakeEntityId = "wake_display";

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
    public const string PlayPauseEntityId = "media_play_pause";
    public const string NextEntityId = "media_next";
    public const string PreviousEntityId = "media_previous";
    public const string StopEntityId = "media_stop";

    protected override IReadOnlyList<EntityDescriptor> Entities { get; } =
    [
        new() { Id = PlayPauseEntityId, Name = "Media play/pause", Kind = EntityKind.Button, Icon = "mdi:play-pause" },
        new() { Id = NextEntityId, Name = "Media next", Kind = EntityKind.Button, Icon = "mdi:skip-next" },
        new() { Id = PreviousEntityId, Name = "Media previous", Kind = EntityKind.Button, Icon = "mdi:skip-previous" },
        new() { Id = StopEntityId, Name = "Media stop", Kind = EntityKind.Button, Icon = "mdi:stop" },
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

/// <summary>
/// Starts programs for the service in the user's session: the service itself runs where nothing it starts could
/// be seen. Runs in the tray app and has no entity of its own; the buttons belong to the service, which sends
/// <see cref="SessionCommands.Launch"/> with what the administrator configured for the button that was pressed.
/// </summary>
public sealed partial class LaunchAction(IEventBus bus, ILogger<LaunchAction> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var commands = bus.Subscribe<ActionCommand>();
        try
        {
            await foreach (var command in commands.ReadAllAsync(stoppingToken))
            {
                if (command.ActionId != SessionCommands.Launch || string.IsNullOrWhiteSpace(command.Value))
                {
                    continue;
                }

                var (file, arguments) = Split(command.Value);
                try
                {
                    // Through the shell, so documents, folders and addresses such as https://… work as well.
                    Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = true })?.Dispose();
                    LogStarted(logger, file);
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
                {
                    LogFailed(logger, file, ex.Message);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Splits a command line into what to start and its arguments: <c>"C:\Program Files\App\app.exe" --flag</c>,
    /// <c>notepad notes.txt</c> or just <c>https://example.com</c>. A path containing spaces must be quoted, unless
    /// it is all there is.
    /// </summary>
    public static (string File, string Arguments) Split(string commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);

        var text = commandLine.Trim();
        if (text.StartsWith('"'))
        {
            var closing = text.IndexOf('"', 1);
            return closing < 0 ? (text[1..], string.Empty) : (text[1..closing], text[(closing + 1)..].TrimStart());
        }

        var space = text.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 || File.Exists(text) || Directory.Exists(text)
            ? (text, string.Empty)
            : (text[..space], text[(space + 1)..].TrimStart());
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Started '{File}' for the service.")]
    private static partial void LogStarted(ILogger logger, string file);

    [LoggerMessage(Level = LogLevel.Error, Message = "Starting '{File}' failed: {Reason}")]
    private static partial void LogFailed(ILogger logger, string file, string reason);
}

/// <summary>
/// Presses key combinations for the service on the user's desktop. Like <see cref="LaunchAction"/> it runs in the
/// tray app and has no entity of its own: the service sends <see cref="SessionCommands.PressKeys"/> with the
/// combination the administrator configured for the button that was pressed.
/// </summary>
public sealed partial class KeyPressAction(IEventBus bus, ILogger<KeyPressAction> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var commands = bus.Subscribe<ActionCommand>();
        try
        {
            await foreach (var command in commands.ReadAllAsync(stoppingToken))
            {
                if (command.ActionId != SessionCommands.PressKeys)
                {
                    continue;
                }

                if (!KeyCombination.TryParse(command.Value, out var combination))
                {
                    LogNotACombination(logger, command.Value);
                }
                else if (!InputSimulator.Press(combination))
                {
                    LogFailed(logger, combination.ToString(), Marshal.GetLastPInvokeError());
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "'{Text}' is not a key combination; nothing was pressed.")]
    private static partial void LogNotACombination(ILogger logger, string? text);

    [LoggerMessage(Level = LogLevel.Error, Message = "Pressing {Combination} failed with error {Error}.")]
    private static partial void LogFailed(ILogger logger, string combination, int error);
}
