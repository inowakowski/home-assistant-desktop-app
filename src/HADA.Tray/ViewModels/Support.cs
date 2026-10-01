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
        _ => entity.Kind == EntityKind.Button ? SymbolRegular.Circle24 : SymbolRegular.Pulse24,
    };

    public static string KindText(EntityDescriptor entity) =>
        Loc.Get(entity.Kind == EntityKind.Button ? "Entity_Button" : "Entity_Sensor");

    /// <summary><c>service</c> or <c>tray:user</c>, as reported by the service.</summary>
    public static string FormatSource(string source) =>
        source == "service" ? Loc.Get("Source_Service")
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
