using System.Collections.ObjectModel;
using System.Windows.Input;
using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Ipc;
using HADA.Tray.Localization;
using HADA.Tray.Mvvm;
using Wpf.Ui.Controls;

namespace HADA.Tray.ViewModels;

public sealed class OverviewViewModel : ObservableObject
{
    private readonly StatusCardViewModel _service;
    private readonly StatusCardViewModel _mqtt;
    private readonly StatusCardViewModel _homeAssistant;
    private readonly StatusCardViewModel _tray;
    private bool _hasNoEntities = true;

    /// <param name="navigate">
    /// Opens the page a status card is about: <c>logs</c>, <c>connections</c>, <c>connections#ha</c> or
    /// <c>entities</c>, as <see cref="MainViewModel.NavigationRequested"/> takes them.
    /// </param>
    public OverviewViewModel(Action<string> navigate)
    {
        _service = new(Loc.Get("Card_Service"), SymbolRegular.Server24, Loc.Get("Card_OpenLogs"), () => navigate("logs"));
        _mqtt = new(Loc.Get("Card_Mqtt"), SymbolRegular.Router24, Loc.Get("Card_OpenConnections"), () => navigate("connections"));
        _homeAssistant = new(
            Loc.Get("Card_HomeAssistant"), SymbolRegular.HomeCheckmark24, Loc.Get("Card_OpenConnections"), () => navigate("connections#ha"));
        _tray = new(Loc.Get("Card_Tray"), SymbolRegular.WindowApps24, Loc.Get("Card_OpenEntities"), () => navigate("entities"));
        Cards = [_service, _mqtt, _homeAssistant, _tray];
        SetUnavailable();
    }

    public IReadOnlyList<StatusCardViewModel> Cards { get; }

    public ObservableCollection<EntityRowViewModel> Entities { get; } = [];

    public bool HasNoEntities
    {
        get => _hasNoEntities;
        private set => SetProperty(ref _hasNoEntities, value);
    }

    public void Update(ServiceStatus status)
    {
        _service.Set(
            Loc.Get("Status_Running"),
            StatusKind.Success,
            Loc.Format("Status_Version", status.Version.Split('+')[0], status.StartedAt.LocalDateTime));
        UpdateEngine(_mqtt, status.Engines.FirstOrDefault(engine => engine.Name == "mqtt"));
        UpdateEngine(_homeAssistant, status.Engines.FirstOrDefault(engine => engine.Name == "websocket"));

        var trayCount = status.SensorClients.Count;
        _tray.Set(
            trayCount > 0 ? Loc.Format("Tray_ClientsConnected", trayCount) : Loc.Get("Tray_NoClients"),
            trayCount > 0 ? StatusKind.Success : StatusKind.Warning,
            Loc.Get("Tray_ClientsDetail"));

        CollectionSync.Sync(
            Entities,
            status.Entities,
            entity => entity.Entity.Id,
            row => row.Id,
            id => new EntityRowViewModel(id),
            (row, entity) => row.Update(entity));
        HasNoEntities = Entities.Count == 0;
    }

    public void SetUnavailable()
    {
        _service.Set(Loc.Get("Status_NotRunning"), StatusKind.Error, Loc.Get("Status_NotRunningDetail"));
        foreach (var card in new[] { _mqtt, _homeAssistant, _tray })
        {
            card.Set("—", StatusKind.Neutral);
        }
    }

    private static void UpdateEngine(StatusCardViewModel card, EngineStatus? engine)
    {
        if (engine is null || !engine.IsConfigured)
        {
            card.Set(Loc.Get("State_NotConfigured"), StatusKind.Neutral, Loc.Get("State_NotConfiguredDetail"));
            return;
        }

        switch (engine.State)
        {
            case EngineConnectionState.Connected:
                card.Set(Loc.Get("State_Connected"), StatusKind.Success);
                break;
            case EngineConnectionState.Connecting:
                card.Set(Loc.Get("State_Connecting"), StatusKind.Warning);
                break;
            case EngineConnectionState.Faulted:
                card.Set(Loc.Get("State_Faulted"), StatusKind.Error, Loc.Get("State_FaultedDetail"));
                break;
            default:
                card.Set(Loc.Get("State_Disconnected"), StatusKind.Warning);
                break;
        }
    }
}

/// <param name="actionText">What clicking the card does, as its tooltip.</param>
/// <param name="open">Opens the page where what the card shows is set up or explained.</param>
public sealed class StatusCardViewModel(string title, SymbolRegular symbol, string actionText, Action open) : ObservableObject
{
    private string _text = string.Empty;
    private string _detail = string.Empty;
    private StatusKind _kind;

    public string Title { get; } = title;

    public SymbolRegular Symbol { get; } = symbol;

    public string ActionText { get; } = actionText;

    public ICommand OpenCommand { get; } = new RelayCommand(open);

    public string Text
    {
        get => _text;
        private set => SetProperty(ref _text, value);
    }

    public string Detail
    {
        get => _detail;
        private set => SetProperty(ref _detail, value);
    }

    public StatusKind Kind
    {
        get => _kind;
        private set => SetProperty(ref _kind, value);
    }

    public void Set(string text, StatusKind kind, string detail = "")
    {
        Text = text;
        Kind = kind;
        Detail = detail;
    }
}

public sealed class EntityRowViewModel(string id) : ObservableObject
{
    private string _name = string.Empty;
    private SymbolRegular _symbol;
    private string _caption = string.Empty;
    private string _valueText = string.Empty;
    private string _sourceText = string.Empty;
    private string _updatedText = string.Empty;
    private bool _isEnabled = true;

    public string Id { get; } = id;

    public string Name
    {
        get => _name;
        private set => SetProperty(ref _name, value);
    }

    public SymbolRegular Symbol
    {
        get => _symbol;
        private set => SetProperty(ref _symbol, value);
    }

    public string Caption
    {
        get => _caption;
        private set => SetProperty(ref _caption, value);
    }

    public string ValueText
    {
        get => _valueText;
        private set => SetProperty(ref _valueText, value);
    }

    public string SourceText
    {
        get => _sourceText;
        private set => SetProperty(ref _sourceText, value);
    }

    public string UpdatedText
    {
        get => _updatedText;
        private set => SetProperty(ref _updatedText, value);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        private set => SetProperty(ref _isEnabled, value);
    }

    public void Update(EntityStatus status)
    {
        var entity = status.Entity;
        Name = entity.Name;
        Symbol = EntityVisuals.SymbolFor(entity);
        IsEnabled = status.IsEnabled;
        Caption = $"{entity.Id} · {(status.IsEnabled ? EntityVisuals.KindText(entity) : Loc.Get("Entity_Disabled"))}";
        ValueText = status.IsAvailable ? EntityVisuals.FormatState(entity, status.State) : Loc.Get("Entity_Unavailable");
        SourceText = EntityVisuals.FormatSource(status.Source);
        UpdatedText = status.UpdatedAt is { } updated ? updated.ToLocalTime().ToString("T", Loc.Culture) : "—";
    }
}
