using HADA.Core.Abstractions;
using HADA.Core.Hosting;
using HADA.Core.Models;
using Microsoft.Extensions.Logging;

namespace HADA.Core.Actions;

/// <summary>
/// The quick actions defined in settings, as the service tells them, and the way to say one was chosen. A quick
/// action does nothing on this computer: choosing it tells Home Assistant, where an automation takes it from there.
/// </summary>
public sealed partial class QuickActions(IEventBus bus, ILogger<QuickActions> logger) : EagerBackgroundService
{
    private volatile IReadOnlyList<QuickActionInfo> _current = [];

    /// <summary>Raised, on a background thread, whenever the service sends the list.</summary>
    public event Action<IReadOnlyList<QuickActionInfo>>? Changed;

    public IReadOnlyList<QuickActionInfo> Current => _current;

    /// <summary>Reports that the user chose a quick action, from the menu or by its shortcut.</summary>
    public void Choose(QuickActionInfo action)
    {
        LogChosen(logger, action.Id);
        _ = bus.PublishAsync(new DeviceEvent { Name = DeviceEvent.QuickAction, Value = action.Id }).AsTask();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var commands = bus.Subscribe<ActionCommand>();
        try
        {
            await foreach (var command in commands.ReadAllAsync(stoppingToken))
            {
                if (command.ActionId == SessionCommands.QuickActions)
                {
                    _current = QuickActionInfo.Deserialize(command.Value);
                    Changed?.Invoke(_current);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Quick action '{Id}' chosen.")]
    private static partial void LogChosen(ILogger logger, string id);
}
