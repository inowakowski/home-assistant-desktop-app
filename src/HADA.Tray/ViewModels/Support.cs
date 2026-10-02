using System.Collections.ObjectModel;
using System.IO;
using HADA.Core.Entities;
using HADA.Ipc;
using HADA.Tray.Localization;
using HADA.Tray.Mvvm;
using Wpf.Ui.Controls;

namespace HADA.Tray.ViewModels;

public enum StatusKind
{
    Neutral,
    Success,
    Warning,
    Error,
}

/// <summary>State of a WPF-UI InfoBar: title, message, severity and whether it is shown.</summary>
public sealed class InfoBarViewModel : ObservableObject
{
    private string _title = string.Empty;
    private string _message = string.Empty;
    private InfoBarSeverity _severity = InfoBarSeverity.Informational;
    private bool _isOpen;

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    public string Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    public InfoBarSeverity Severity
    {
        get => _severity;
        private set => SetProperty(ref _severity, value);
    }

    public bool IsOpen
    {
        get => _isOpen;
        set => SetProperty(ref _isOpen, value);
    }

    public void Show(string title, string message, InfoBarSeverity severity)
    {
        Title = title;
        Message = message;
        Severity = severity;
        IsOpen = true;
    }

    public void Close() => IsOpen = false;
}

internal static class ServiceErrors
{
    /// <summary>Failures the window handles itself instead of treating as bugs.</summary>
    public static bool IsExpected(Exception exception) =>
        exception is ServiceUnavailableException or ServiceControlException or TimeoutException
            or InvalidDataException or IOException or ObjectDisposedException;

    public static string Describe(Exception exception) => exception switch
    {
        ServiceUnavailableException => Loc.Get("Error_ServiceUnavailable"),
        ServiceControlException { Error: IpcError.Unauthorized } => Loc.Get("Error_Unauthorized"),
        _ => exception.Message,
    };
}

internal static class EntityVisuals
{
    public static SymbolRegular SymbolFor(EntityDescriptor entity) => entity.Id switch
    {
        "cpu_load" => SymbolRegular.DeveloperBoard24,
        "active_window" => SymbolRegular.Window24,
        "audio_volume" => SymbolRegular.Speaker224,
        "lock_screen" => SymbolRegular.LockClosed24,
        "memory_usage" => SymbolRegular.Storage24,
        "battery_level" => SymbolRegular.Battery524,
        "battery_charging" => SymbolRegular.BatteryCharge24,
        "plugged_in" => SymbolRegular.PlugConnected24,
        "display_on" => SymbolRegular.Desktop24,
        "lid_open" => SymbolRegular.Laptop24,
        "session_locked" => SymbolRegular.LockClosed24,
        "last_boot" => SymbolRegular.Clock24,
        "user_active" => SymbolRegular.Person24,
        "microphone_in_use" => SymbolRegular.Mic24,
        "microphone_muted" => SymbolRegular.MicOff24,
        "camera_in_use" => SymbolRegular.Video24,
        _ => entity.Kind switch
        {
            EntityKind.Button => SymbolRegular.Circle24,
            EntityKind.BinarySensor => SymbolRegular.ToggleLeft24,
            _ => SymbolRegular.Pulse24,
        },
    };

    public static string KindText(EntityDescriptor entity) => Loc.Get(entity.Kind switch
    {
        EntityKind.Button => "Entity_Button",
        EntityKind.BinarySensor => "Entity_BinarySensor",
        _ => "Entity_Sensor",
    });

    /// <summary>The state as shown in the window: on/off in the user's language, numbers with their unit.</summary>
    public static string FormatState(EntityDescriptor entity, string? state)
    {
        if (entity.Kind == EntityKind.Button || state is null)
        {
            return "—";
        }

        if (entity.Kind == EntityKind.BinarySensor)
        {
            return state == BinaryState.On ? Loc.Get("Common_On") : state == BinaryState.Off ? Loc.Get("Common_Off") : state;
        }

        if (entity.DeviceClass == "timestamp" && DateTimeOffset.TryParse(state, Loc.Culture, out var timestamp))
        {
            return timestamp.ToLocalTime().ToString("g", Loc.Culture);
        }

        return entity.UnitOfMeasurement is { } unit ? $"{state} {unit}" : state;
    }

    /// <summary><c>service</c>, <c>custom</c> or <c>tray:user</c>, as reported by the service.</summary>
    public static string FormatSource(string source) =>
        source == "service" ? Loc.Get("Source_Service")
        : source == "custom" ? Loc.Get("Source_Custom")
        : source.StartsWith("tray:", StringComparison.Ordinal) ? Loc.Format("Source_Tray", source["tray:".Length..])
        : source;

    public static string HintFor(string entityId) => Loc.TryGet($"EntityHint_{entityId}") ?? string.Empty;
}

internal static class CollectionSync
{
    /// <summary>Updates rows in place, keyed by id, so the list does not flicker or lose scroll position on refresh.</summary>
    public static void Sync<TRow, TItem>(
        ObservableCollection<TRow> rows,
        IReadOnlyList<TItem> items,
        Func<TItem, string> itemKey,
        Func<TRow, string> rowKey,
        Func<string, TRow> create,
        Action<TRow, TItem> update)
    {
        var wanted = items.Select(itemKey).ToHashSet(StringComparer.Ordinal);
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(rowKey(rows[i])))
            {
                rows.RemoveAt(i);
            }
        }

        for (var index = 0; index < items.Count; index++)
        {
            var key = itemKey(items[index]);
            var row = rows.FirstOrDefault(candidate => rowKey(candidate) == key);
            if (row is null)
            {
                row = create(key);
                rows.Insert(index, row);
            }
            else if (rows.IndexOf(row) is var current && current != index)
            {
                rows.Move(current, index);
            }

            update(row, items[index]);
        }
    }
}
