using System.Collections.ObjectModel;
using System.Windows.Input;
using HADA.Presentation.Localization;
using HADA.Presentation.Mvvm;

namespace HADA.Presentation.ViewModels;

/// <summary>The two ways HADA reaches a Home Assistant; the direct one has variants, set per server.</summary>
public enum ConnectionWay
{
    Mqtt,
    HomeAssistant,
}

/// <summary>The servers of one way of connecting, shown together; a way without servers is not shown at all.</summary>
public sealed class ConnectionGroupViewModel(ConnectionWay way) : ObservableObject
{
    private string _summary = string.Empty;

    public ConnectionWay Way { get; } = way;

    public string Key => Way.ToString();

    public string Title { get; } = Loc.Get(way == ConnectionWay.Mqtt ? "Connections_GroupMqtt" : "Connections_GroupDirect");

    public Glyph Symbol { get; } = way == ConnectionWay.Mqtt ? Glyph.Router : Glyph.HomeCheckmark;

    public bool IsMqtt => Way == ConnectionWay.Mqtt;

    /// <summary>How many of them are connected, e.g. "1 of 2 connected"; empty where that is not told.</summary>
    public string Summary
    {
        get => _summary;
        set => SetProperty(ref _summary, value);
    }

    public ObservableCollection<ConnectionRowViewModel> Rows { get; } = [];
}

/// <summary>One server in a list: its name, a line about it, and how it is doing.</summary>
public sealed class ConnectionRowViewModel(string id, Action open) : ObservableObject
{
    private string _name = string.Empty;
    private string _detail = string.Empty;
    private string _statusText = string.Empty;
    private StatusKind _kind;

    public string Id { get; } = id;

    public ICommand OpenCommand { get; } = new RelayCommand(open);

    public string Name
    {
        get => _name;
        private set => SetProperty(ref _name, value);
    }

    /// <summary>Its address and how it is used; empty where the list shows names only.</summary>
    public string Detail
    {
        get => _detail;
        private set
        {
            if (SetProperty(ref _detail, value))
            {
                OnPropertyChanged(nameof(HasDetail));
            }
        }
    }

    public bool HasDetail => Detail.Length > 0;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public StatusKind Kind
    {
        get => _kind;
        private set
        {
            if (SetProperty(ref _kind, value))
            {
                OnPropertyChanged(nameof(IsProblem));
            }
        }
    }

    /// <summary>Whether the status is to stand out: the connection does not work, or not fully.</summary>
    public bool IsProblem => Kind == StatusKind.Error;

    public void Set(string name, string detail, string statusText, StatusKind kind)
    {
        Name = name;
        Detail = detail;
        StatusText = statusText;
        Kind = kind;
    }
}

/// <summary>A connection that does not work, or not fully, as it is told at the top of the Overview.</summary>
/// <param name="Key">What tells this problem from another: the server and what is wrong with it.</param>
/// <param name="Target">The page and section that show the server, as <see cref="MainViewModel.NavigationRequested"/> takes them.</param>
public sealed record ConnectionProblem(string Key, string Name, string Way, string Detail, string Target);

/// <summary>
/// The notice at the top of the Overview while a connection needs the user. Closing it hides it until something
/// else goes wrong: another connection, or this one again after it worked in between.
/// </summary>
public sealed class AttentionViewModel : ObservableObject
{
    private string? _key;
    private string? _dismissedKey;
    private string? _target;
    private string _title = string.Empty;
    private string _detail = string.Empty;
    private bool _isOpen;

    /// <param name="navigate">Opens the page of the connection the notice is about.</param>
    public AttentionViewModel(Action<string> navigate)
    {
        ShowCommand = new RelayCommand(() =>
        {
            if (_target is { } target)
            {
                navigate(target);
            }
        });
        DismissCommand = new RelayCommand(() =>
        {
            _dismissedKey = _key;
            IsOpen = false;
        });
    }

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    public string Detail
    {
        get => _detail;
        private set => SetProperty(ref _detail, value);
    }

    public bool IsOpen
    {
        get => _isOpen;
        private set => SetProperty(ref _isOpen, value);
    }

    public ICommand ShowCommand { get; }

    public ICommand DismissCommand { get; }

    /// <summary>Tells the notice which connections need attention now; none closes it and forgets what was closed.</summary>
    public void Update(IReadOnlyList<ConnectionProblem> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);
        if (problems.Count == 0)
        {
            _key = null;
            _dismissedKey = null;
            _target = null;
            IsOpen = false;
            return;
        }

        var first = problems[0];
        _key = string.Join('\n', problems.Select(problem => problem.Key));
        _target = first.Target;
        Title = problems.Count == 1 ? Loc.Format("Attention_One", first.Name, first.Way) : Loc.Format("Attention_Several", problems.Count);
        Detail = problems.Count == 1 ? first.Detail : string.Join(", ", problems.Select(problem => problem.Name));
        IsOpen = _key != _dismissedKey;
    }
}
