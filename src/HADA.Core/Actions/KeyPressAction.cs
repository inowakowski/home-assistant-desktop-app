using HADA.Core.Abstractions;
using HADA.Core.Hosting;
using HADA.Core.Input;
using HADA.Core.Models;
using Microsoft.Extensions.Logging;

namespace HADA.Core.Actions;

/// <summary>
/// Presses key combinations for the service on the user's desktop. Like <see cref="LaunchAction"/> it runs in the
/// user's session and has no entity of its own: the service sends <see cref="SessionCommands.PressKeys"/> with the
/// combination the administrator configured for the button that was pressed.
/// </summary>
public sealed partial class KeyPressAction(IEventBus bus, ILogger<KeyPressAction> logger, IKeyboard keyboard) : EagerBackgroundService
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
                else if (keyboard.Press(combination) is { } reason)
                {
                    LogFailed(logger, combination.ToString(), reason);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "'{Text}' is not a key combination; nothing was pressed.")]
    private static partial void LogNotACombination(ILogger logger, string? text);

    [LoggerMessage(Level = LogLevel.Error, Message = "Pressing {Combination} failed: {Reason}")]
    private static partial void LogFailed(ILogger logger, string combination, string reason);
}
