using System.Collections.ObjectModel;
using HADA.Core.Entities;
using HADA.Ipc;
using HADA.Tray.Localization;
using HADA.Tray.Mvvm;
using Wpf.Ui.Controls;

namespace HADA.Tray.ViewModels;

/// <summary>What a connection does not carry to Home Assistant, said on the Entities page.</summary>
/// <param name="Connection">The name of the connection's Home Assistant.</param>
public sealed record ReachNote(string Connection, string Text);

public sealed class EntitiesViewModel : ObservableObject
{
    private bool _hasNoEntities = true;

    public EntitiesViewModel(SettingsViewModel settings)
    {
        Settings = settings;
        settings.SnapshotApplied += (_, _) => UpdateReach();
        settings.DisabledEntitiesChanged += (_, _) =>
        {
            foreach (var row in Rows)
            {
                row.RefreshEnabled();
            }
        };
    }

    public SettingsViewModel Settings { get; }

    public ObservableCollection<EntityToggleViewModel> Rows { get; } = [];

    public bool HasNoEntities
    {
        get => _hasNoEntities;
        private set => SetProperty(ref _hasNoEntities, value);
    }

    /// <summary>
    /// One note for each connection that carries less than the list below offers, so that an entity switched on
    /// here and missing in Home Assistant is no riddle. Empty when every connection carries everything.
    /// </summary>
    public ObservableCollection<ReachNote> ReachNotes { get; } = [];

    public bool HasReachNotes => ReachNotes.Count > 0;

    private void UpdateReach()
    {
        var mqtt = Settings.SavedMqttServers.Where(server => server.Host.Trim().Length > 0).ToList();
        var direct = Settings.SavedHomeAssistantServers.Where(server => server.BaseUrl.Trim().Length > 0).ToList();

        // Through MQTT and through the HADA integration everything arrives.
        var carriesAll = mqtt.Count > 0 || direct.Exists(server => server.SensorMode == HomeAssistantSensorMode.Integration);
        ReachNotes.Clear();
        foreach (var server in direct)
        {
            var key = server.SensorMode switch
            {
                HomeAssistantSensorMode.Entities => "Reach_Entities",
                HomeAssistantSensorMode.States => "Reach_States",

                // Beside a connection that carries the entities, one for notifications alone is as it is meant to be.
                HomeAssistantSensorMode.Off when !carriesAll => server.Notifications ? "Reach_NotificationsOnly" : "Reach_Nothing",
                _ => null,
            };
            if (key is not null)
            {
                ReachNotes.Add(new ReachNote(NameOf(server), Loc.Get(key)));
            }
        }

        if (mqtt.Count == 0 && direct.Count == 0 && Settings.IsLoaded)
        {
            ReachNotes.Add(new ReachNote(Loc.Get("Reach_NoConnectionTitle"), Loc.Get("Reach_NoConnection")));
        }

        OnPropertyChanged(nameof(HasReachNotes));
    }

    private static string NameOf(HomeAssistantSettings server) =>
        server.Name.Length > 0 ? server.Name
        : Uri.TryCreate(server.BaseUrl, UriKind.Absolute, out var url) ? url.Host
        : server.BaseUrl;

    public void Update(ServiceStatus status)
    {
        CollectionSync.Sync(
            Rows,
            status.Entities,
            entity => entity.Entity.Id,
            row => row.Id,
            id => new EntityToggleViewModel(id, Settings),
            (row, entity) => row.Update(entity));
        HasNoEntities = Rows.Count == 0;
    }
}

/// <summary>An entity with a switch that edits the pending settings, not the service directly.</summary>
public sealed class EntityToggleViewModel(string id, SettingsViewModel settings) : ObservableObject
{
    private string _name = string.Empty;
    private SymbolRegular _symbol;
    private string _caption = string.Empty;
    private string _hint = string.Empty;
    private bool _enabledByDefault = true;

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

    public string Hint
    {
        get => _hint;
        private set
        {
            if (SetProperty(ref _hint, value))
            {
                OnPropertyChanged(nameof(HasHint));
            }
        }
    }

    public bool HasHint => Hint.Length > 0;

    public bool IsEnabled
    {
        get => settings.IsEntityEnabled(Id, _enabledByDefault);
        set
        {
            settings.SetEntityEnabled(Id, _enabledByDefault, value);
            OnPropertyChanged();
        }
    }

    public void Update(EntityStatus status)
    {
        var entity = status.Entity;
        Name = entity.Name;
        Symbol = EntityVisuals.SymbolFor(entity);
        Caption = $"{entity.Id} · {EntityVisuals.KindText(entity)} · {EntityVisuals.FormatSource(status.Source)}";
        Hint = EntityVisuals.HintFor(entity.Id);
        if (_enabledByDefault != entity.EnabledByDefault)
        {
            _enabledByDefault = entity.EnabledByDefault;
            RefreshEnabled();
        }
    }

    public void RefreshEnabled() => OnPropertyChanged(nameof(IsEnabled));
}
