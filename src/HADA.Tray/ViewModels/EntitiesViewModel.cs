using System.Collections.ObjectModel;
using HADA.Ipc;
using HADA.Tray.Mvvm;
using Wpf.Ui.Controls;

namespace HADA.Tray.ViewModels;

public sealed class EntitiesViewModel : ObservableObject
{
    private bool _hasNoEntities = true;

    public EntitiesViewModel(SettingsViewModel settings)
    {
        Settings = settings;
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
        get => !settings.IsEntityDisabled(Id);
        set
        {
            settings.SetEntityDisabled(Id, !value);
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
    }

    public void RefreshEnabled() => OnPropertyChanged(nameof(IsEnabled));
}
