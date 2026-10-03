using System.Runtime.InteropServices;
using System.Windows.Interop;
using HADA.Core.Abstractions;
using HADA.Core.Hosting;
using HADA.Core.Input;
using HADA.Core.Models;
using HADA.Platform.Windows.Actions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HADA.Tray.Session;

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

/// <summary>
/// Keyboard shortcuts that work whichever program has the focus. Belongs to the thread that creates it, which must
/// be one that pumps messages: Windows posts the key presses to that thread.
/// </summary>
public sealed partial class GlobalHotkeys : IDisposable
{
    private const int HotkeyMessage = 0x0312;
    private const uint NoRepeat = 0x4000;

    private readonly Dictionary<int, Action> _actions = [];
    private readonly ILogger _logger;
    private int _lastId;

    public GlobalHotkeys(ILogger logger)
    {
        _logger = logger;
        ComponentDispatcher.ThreadFilterMessage += OnThreadMessage;
    }

    /// <summary>Replaces every shortcut registered so far.</summary>
    /// <param name="shortcuts">Shortcut as written in settings, what to call it in the log, and what to do when it is pressed.</param>
    public void Set(IEnumerable<(string Shortcut, string Name, Action Pressed)> shortcuts)
    {
        Clear();
        foreach (var (shortcut, name, pressed) in shortcuts)
        {
            if (!KeyCombination.TryParse(shortcut, out var keys) || keys.VirtualKey == 0)
            {
                continue;
            }

            // KeyModifiers has the values Windows gives these modifiers.
            var id = ++_lastId;
            if (RegisterHotKey(0, id, (uint)keys.Modifiers | NoRepeat, keys.VirtualKey))
            {
                _actions[id] = pressed;
            }
            else
            {
                // Usually another program holds the same shortcut; the menu entry still works.
                LogNotRegistered(_logger, shortcut, name, Marshal.GetLastPInvokeError());
            }
        }
    }

    public void Dispose()
    {
        Clear();
        ComponentDispatcher.ThreadFilterMessage -= OnThreadMessage;
    }

    private void Clear()
    {
        foreach (var id in _actions.Keys)
        {
            UnregisterHotKey(0, id);
        }

        _actions.Clear();
    }

    private void OnThreadMessage(ref MSG message, ref bool handled)
    {
        if (message.message == HotkeyMessage && _actions.TryGetValue((int)message.wParam, out var pressed))
        {
            handled = true;
            pressed();
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint window, int id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The shortcut {Shortcut} of quick action '{Name}' could not be registered (error {Error}); another program probably uses it.")]
    private static partial void LogNotRegistered(ILogger logger, string shortcut, string name, int error);
}
