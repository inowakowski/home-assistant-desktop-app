using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using HADA.Ipc;
using HADA.Tray.Localization;
using HADA.Tray.Mvvm;
using Microsoft.Extensions.Logging;

namespace HADA.Tray.ViewModels;

public sealed class LogsViewModel : ObservableObject
{
    private const int MaxEntries = 1000;
    private const int PageSize = 50;
    private const int MaxPagesPerRefresh = 10;

    private readonly ServiceControlClient _client;
    private readonly RelayCommand _copyCommand;
    private readonly RelayCommand _clearCommand;
    private long _lastSequence;
    private DateTimeOffset? _serviceStartedAt;
    private LevelOption _minimumLevel;
    private bool _autoScroll = true;

    public LogsViewModel(ServiceControlClient client)
    {
        _client = client;
        LevelOptions =
        [
            new(LogLevel.Trace, Loc.Get("Logs_LevelAll")),
            new(LogLevel.Information, Loc.Get("Logs_LevelInformation")),
            new(LogLevel.Warning, Loc.Get("Logs_LevelWarning")),
            new(LogLevel.Error, Loc.Get("Logs_LevelError")),
        ];
        _minimumLevel = LevelOptions[0];

        View = CollectionViewSource.GetDefaultView(Entries);
        View.Filter = item => ((LogEntryViewModel)item).Level >= _minimumLevel.Level;

        _copyCommand = new RelayCommand(Copy, () => Entries.Count > 0);
        _clearCommand = new RelayCommand(Clear, () => Entries.Count > 0);
        Entries.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasNoEntries));
            _copyCommand.RaiseCanExecuteChanged();
            _clearCommand.RaiseCanExecuteChanged();
        };
    }

    /// <summary>Raised after new entries arrive, so the page can scroll to the end.</summary>
    public event EventHandler? EntriesAdded;

    public IReadOnlyList<LevelOption> LevelOptions { get; }

    public LevelOption MinimumLevel
    {
        get => _minimumLevel;
        set
        {
            if (SetProperty(ref _minimumLevel, value))
            {
                View.Refresh();
            }
        }
    }

    public bool AutoScroll
    {
        get => _autoScroll;
        set => SetProperty(ref _autoScroll, value);
    }

    public ObservableCollection<LogEntryViewModel> Entries { get; } = [];

    public ICollectionView View { get; }

    public bool HasNoEntries => Entries.Count == 0;

    public ICommand CopyCommand => _copyCommand;

    public ICommand ClearCommand => _clearCommand;

    /// <summary>A restarted service numbers its log entries from 1 again, so start reading from the latest entries.</summary>
    public void OnServiceStarted(DateTimeOffset startedAt)
    {
        if (_serviceStartedAt != startedAt)
        {
            _serviceStartedAt = startedAt;
            _lastSequence = 0;
        }
    }

    public async Task RefreshAsync()
    {
        var added = false;
        for (var page = 0; page < MaxPagesPerRefresh; page++)
        {
            var batch = await _client.GetLogsAsync(_lastSequence);
            foreach (var entry in batch)
            {
                Entries.Add(new LogEntryViewModel(entry));
                _lastSequence = entry.Sequence;
            }

            added |= batch.Count > 0;
            if (batch.Count < PageSize)
            {
                break;
            }
        }

        while (Entries.Count > MaxEntries)
        {
            Entries.RemoveAt(0);
        }

        if (added)
        {
            EntriesAdded?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Copy()
    {
        var text = string.Join(Environment.NewLine, View.Cast<LogEntryViewModel>().Select(entry => entry.FullText));
        try
        {
            Clipboard.SetText(text);
        }
        catch (COMException)
        {
            // Another application is holding the clipboard; the user can simply try again.
        }
    }

    private void Clear() => Entries.Clear();
}

public sealed record LevelOption(LogLevel Level, string Name)
{
    public override string ToString() => Name;
}

public sealed class LogEntryViewModel(LogEntry entry)
{
    public LogLevel Level => entry.Level;

    public string Time => entry.Timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public string LevelText => entry.Level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "CRIT",
        _ => string.Empty,
    };

    /// <summary>The class name without its namespace, e.g. <c>MqttEngine</c>.</summary>
    public string Category => entry.Category[(entry.Category.LastIndexOf('.') + 1)..];

    public string FullCategory => entry.Category;

    public string Message => entry.Message;

    public string? ExceptionText => entry.Exception;

    public bool HasException => entry.Exception is not null;

    public string FullText =>
        $"{entry.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss} {LevelText} {entry.Category}: {entry.Message}"
        + (entry.Exception is null ? string.Empty : Environment.NewLine + entry.Exception);
}
