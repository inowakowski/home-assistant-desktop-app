using System.Runtime.CompilerServices;
using System.Windows.Input;
using HADA.Core;
using HADA.Core.Entities;
using HADA.Ipc;
using HADA.Platform.Windows.Network;
using HADA.Tray.Localization;
using HADA.Tray.Mvvm;

namespace HADA.Tray.ViewModels;

public sealed record SensorModeOption(HomeAssistantSensorMode Mode, string Label);

/// <summary>A network the computer is connected to right now, offered for adding to a server's home networks.</summary>
public sealed record NetworkChoice(string Name, ICommand Add);

/// <summary>
/// One Home Assistant connected to directly, on the Connections page. Its access token is never shown: an empty
/// field keeps the saved one.
/// </summary>
public sealed class HomeAssistantServerViewModel : ObservableObject
{
    public const string DefaultCommandEventType = "hada_command";

    private readonly Action _changed;
    private string _name;
    private string _url;
    private string _accessToken = string.Empty;
    private bool _clearAccessToken;
    private string _deviceId;
    private string _deviceName;
    private string _commandEventType;
    private bool _notifications;
    private HomeAssistantSensorMode _sensorMode;
    private bool _allowUnencrypted;
    private string _localUrl;
    private string _localNetworks;
    private readonly string _savedClientId;
    private HomeAssistantSignInResult? _signIn;
    private string _signInStatus = string.Empty;

    /// <param name="changed">Called after anything was typed, to find out whether there is something to save.</param>
    public HomeAssistantServerViewModel(HomeAssistantSettings settings, bool hasAccessToken, Action changed)
    {
        _changed = changed;
        Id = settings.Id;
        HasAccessToken = hasAccessToken;
        _name = settings.Name;
        _url = settings.BaseUrl;
        _deviceId = settings.DeviceId;
        _deviceName = settings.DeviceName;
        _commandEventType = settings.CommandEventType;
        _notifications = settings.Notifications;
        _sensorMode = settings.SensorMode;
        _allowUnencrypted = settings.AllowUnencrypted;
        _localUrl = settings.LocalUrl;
        _savedClientId = settings.ClientId;
        IsSavedSignIn = hasAccessToken && settings.ClientId.Length > 0;
        SignInCommand = new AsyncCommand(SignInAsync);

        // One per line in the box, whatever line ends they were saved with.
        _localNetworks = string.Join(Environment.NewLine, NetworkNames(settings.LocalNetworks));
        ConnectedNetworks =
        [
            .. WindowsNetworkLocation.Read().Select(name => new NetworkChoice(name, new RelayCommand(() => AddHomeNetwork(name)))),
        ];
    }

    /// <summary>A server that was just added: nothing typed yet, and an id of its own.</summary>
    /// <param name="sensors">False when Home Assistant gets the sensors through MQTT already, so that it does not get them twice.</param>
    public static HomeAssistantServerViewModel New(Action changed, bool sensors) =>
        new(new HomeAssistantSettings(string.Empty, string.Empty, string.Empty, DefaultCommandEventType)
        {
            Id = MqttSettings.NewId(),
            Notifications = true,
            SensorMode = sensors ? HomeAssistantSensorMode.Entities : HomeAssistantSensorMode.Off,
        },
        hasAccessToken: false,
        changed);

    public string Id { get; }

    public bool HasAccessToken { get; }

    /// <summary>Whether what is saved is a sign-in made through Home Assistant, not a token that was pasted.</summary>
    public bool IsSavedSignIn { get; }

    public string AccessTokenPlaceholder =>
        Loc.Get(IsSavedSignIn ? "Placeholder_SignedIn" : HasAccessToken ? "Placeholder_SecretSaved" : "Placeholder_SecretNone");

    /// <summary>Signs in through Home Assistant in the browser, as the other way to a token than pasting one.</summary>
    public ICommand SignInCommand { get; }

    /// <summary>How the sign-in is going, or went; empty before one was tried.</summary>
    public string SignInStatus
    {
        get => _signInStatus;
        private set
        {
            if (SetProperty(ref _signInStatus, value))
            {
                OnPropertyChanged(nameof(SignInNote));
            }
        }
    }

    /// <summary>Under the button: what it does, until it was pressed; then how that went.</summary>
    public string SignInNote => SignInStatus.Length > 0 ? SignInStatus : Loc.Get("SignIn_Hint");

    private async Task SignInAsync()
    {
        var typed = Url.Trim();
        if (!Uri.TryCreate(typed.EndsWith('/') ? typed : typed + "/", UriKind.Absolute, out var baseUrl)
            || (baseUrl.Scheme != Uri.UriSchemeHttp && baseUrl.Scheme != Uri.UriSchemeHttps))
        {
            SignInStatus = Loc.Get("SignIn_NeedsAddress");
            return;
        }

        // What comes back opens as much as a password does; it travels no way the access token may not.
        if (IsUnencryptedPublic && !AllowUnencrypted)
        {
            SignInStatus = Loc.Get("SignIn_Unencrypted");
            return;
        }

        try
        {
            _signIn = await HomeAssistantSignIn.RunAsync(
                baseUrl,
                start =>
                {
                    // Said before the browser is opened, with the address to open by hand should none come up.
                    SignInStatus = Loc.Format("SignIn_Waiting", start.AbsoluteUri);
                    HomeAssistantSignIn.OpenInBrowser(start);
                },
                CancellationToken.None);

            // The token box holds what the sign-in gave, as it would a pasted token, until it is saved.
            AccessToken = _signIn.RefreshToken;
            ClearAccessToken = false;
            _changed();
            SignInStatus = Loc.Get("SignIn_Done");
        }
        catch (TimeoutException)
        {
            SignInStatus = Loc.Get("SignIn_TimedOut");
        }
        catch (Exception ex) when (ex is HomeAssistantSignInExpiredException or System.Net.Http.HttpRequestException or System.IO.InvalidDataException
            or System.ComponentModel.Win32Exception or System.Net.Sockets.SocketException or OperationCanceledException)
        {
            SignInStatus = Loc.Format("SignIn_Failed", ex.Message);
        }
    }

    public string MachineNamePlaceholder { get; } = Loc.Format("Placeholder_MachineName", Environment.MachineName);

    /// <summary>How the server is listed: its name, else its address, else that it is new.</summary>
    public string DisplayName =>
        Name.Trim() is { Length: > 0 } name ? name
        : Uri.TryCreate(Url.Trim(), UriKind.Absolute, out var url) && url.Host.Length > 0 ? url.Host
        : Url.Trim() is { Length: > 0 } typed ? typed
        : Loc.Get("Mqtt_NewServer");

    /// <summary>Nothing typed into it: not a server yet, and not saved.</summary>
    public bool IsBlank => Name.Trim().Length == 0 && Url.Trim().Length == 0;

    public string Name
    {
        get => _name;
        set
        {
            if (SetSetting(ref _name, value))
            {
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }

    public string Url
    {
        get => _url;
        set
        {
            if (SetSetting(ref _url, value))
            {
                OnPropertyChanged(nameof(DisplayName));
                OnPropertyChanged(nameof(EncryptionNote));
                OnPropertyChanged(nameof(HasEncryptionNote));
                OnPropertyChanged(nameof(IsUnencryptedPublic));
                OnPropertyChanged(nameof(CanAllowUnencrypted));
            }
        }
    }

    /// <summary>
    /// Says under the address whether what is sent there is encrypted, and what that means for an address that is
    /// not; empty until the address is one.
    /// </summary>
    public string EncryptionNote =>
        !Uri.TryCreate(Url.Trim(), UriKind.Absolute, out var url) || url.Host.Length == 0 ? string.Empty
        : url.Scheme == Uri.UriSchemeHttps ? Loc.Get("Ha_UrlEncrypted")
        : url.Scheme != Uri.UriSchemeHttp ? string.Empty
        : NetworkScope.LooksLocal(url.DnsSafeHost) ? Loc.Get("Ha_UrlUnencryptedLocal")
        : Loc.Get(AllowUnencrypted ? "Ha_UrlUnencryptedAllowed" : "Ha_UrlUnencryptedPublic");

    public bool HasEncryptionNote => EncryptionNote.Length > 0;

    /// <summary>
    /// An <c>http</c> address that reads like one out on the internet. Whether it is one is found out when
    /// connecting, by what the name stands for; this only decides how the note is worded and coloured.
    /// </summary>
    public bool IsUnencryptedPublic =>
        Uri.TryCreate(Url.Trim(), UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttp
        && url.Host.Length > 0 && !NetworkScope.LooksLocal(url.DnsSafeHost);

    /// <summary>The switch is offered where it has a say, and stays in sight while it is on.</summary>
    public bool CanAllowUnencrypted => IsUnencryptedPublic || AllowUnencrypted;

    /// <summary>The address used instead of <see cref="Url"/> while the computer is on one of the home networks.</summary>
    public string LocalUrl
    {
        get => _localUrl;
        set
        {
            if (SetSetting(ref _localUrl, value))
            {
                OnHomeNetworkChanged();
            }
        }
    }

    /// <summary>The home networks, by their names, one per line.</summary>
    public string LocalNetworks
    {
        get => _localNetworks;
        set
        {
            if (SetSetting(ref _localNetworks, value ?? string.Empty))
            {
                OnHomeNetworkChanged();
            }
        }
    }

    /// <summary>The networks the computer is on right now, by the names Windows knows them by; each can be added with a click.</summary>
    public IReadOnlyList<NetworkChoice> ConnectedNetworks { get; }

    public bool HasConnectedNetworks => ConnectedNetworks.Count > 0;

    /// <summary>
    /// What is missing for the local address to be used: the address itself, or a network to use it on. Empty
    /// when both are there, and when neither is.
    /// </summary>
    public string HomeNetworkNote
    {
        get
        {
            var hasAddress = LocalUrl.Trim().Length > 0;
            var hasNetworks = NetworkNames(LocalNetworks).Count > 0;
            return hasAddress == hasNetworks ? string.Empty : Loc.Get(hasAddress ? "Ha_HomeNoNetworks" : "Ha_HomeNoAddress");
        }
    }

    public bool HasHomeNetworkNote => HomeNetworkNote.Length > 0;

    /// <summary>Whether the section opens by itself: it does where something is set up in it.</summary>
    public bool UsesHomeNetwork => LocalUrl.Trim().Length > 0 || NetworkNames(LocalNetworks).Count > 0;

    private static IReadOnlyList<string> NetworkNames(string networks) => NetworkScope.Names(networks);

    private void AddHomeNetwork(string name)
    {
        var names = NetworkNames(LocalNetworks);
        if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            LocalNetworks = string.Join(Environment.NewLine, [.. names, name]);
        }
    }

    private void OnHomeNetworkChanged()
    {
        OnPropertyChanged(nameof(HomeNetworkNote));
        OnPropertyChanged(nameof(HasHomeNetworkNote));
    }

    public bool AllowUnencrypted
    {
        get => _allowUnencrypted;
        set
        {
            if (SetSetting(ref _allowUnencrypted, value))
            {
                OnPropertyChanged(nameof(EncryptionNote));
                OnPropertyChanged(nameof(CanAllowUnencrypted));
            }
        }
    }

    public string AccessToken { get => _accessToken; set => SetSetting(ref _accessToken, value ?? string.Empty); }

    /// <summary>
    /// The client id that goes with the token as it would be saved: that of the sign-in just made, when the box
    /// still holds what it gave; the saved one, when the box is empty and the saved token is kept; and none for
    /// a token that was typed or pasted, which is a long-lived one.
    /// </summary>
    private string ClientIdToSave =>
        ClearAccessToken ? string.Empty
        : AccessToken.Length == 0 ? _savedClientId
        : _signIn is { } signIn && signIn.RefreshToken == AccessToken ? signIn.ClientId
        : string.Empty;

    public bool ClearAccessToken { get => _clearAccessToken; set => SetSetting(ref _clearAccessToken, value); }

    public string DeviceId { get => _deviceId; set => SetSetting(ref _deviceId, value); }

    public string DeviceName { get => _deviceName; set => SetSetting(ref _deviceName, value); }

    public string CommandEventType { get => _commandEventType; set => SetSetting(ref _commandEventType, value); }

    public bool Notifications { get => _notifications; set => SetSetting(ref _notifications, value); }

    public HomeAssistantSensorMode SensorMode
    {
        get => _sensorMode;
        set
        {
            if (SetSetting(ref _sensorMode, value))
            {
                OnPropertyChanged(nameof(SensorModeDescription));
            }
        }
    }

    /// <summary>What the chosen way means, in a sentence or two, shown under the list.</summary>
    public string SensorModeDescription => Loc.TryGet($"Ha_SensorsDetail_{SensorMode}") ?? string.Empty;

    public IReadOnlyList<SensorModeOption> SensorModes { get; } =
    [
        new(HomeAssistantSensorMode.Off, Loc.Get("Ha_SensorsOff")),
        new(HomeAssistantSensorMode.Entities, Loc.Get("Ha_SensorsEntities")),
        new(HomeAssistantSensorMode.Integration, Loc.Get("Ha_SensorsIntegration")),
        new(HomeAssistantSensorMode.States, Loc.Get("Ha_SensorsStates")),
    ];

    /// <summary>The server as it would be saved.</summary>
    public HomeAssistantServerUpdate ToUpdate() => new(
        new HomeAssistantSettings(Url.Trim(), DeviceId.Trim(), DeviceName.Trim(), CommandEventType.Trim())
        {
            Id = Id,
            Name = Name.Trim(),
            Notifications = Notifications,
            SensorMode = SensorMode,
            AllowUnencrypted = AllowUnencrypted,
            LocalUrl = LocalUrl.Trim(),
            LocalNetworks = string.Join('\n', NetworkNames(LocalNetworks)),

            ClientId = ClientIdToSave,
        },
        SettingsViewModel.SecretUpdateFor(AccessToken, ClearAccessToken));

    /// <param name="urlRequired">For a connection test, which needs somewhere to connect to.</param>
    public string? Validate(bool urlRequired)
    {
        var url = Url.Trim();
        if (urlRequired && url.Length == 0)
        {
            return Loc.Get("Validation_UrlRequired");
        }

        if (url.Length > 0
            && !(Uri.TryCreate(url, UriKind.Absolute, out var parsed) && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps)))
        {
            return Loc.Get("Validation_Url");
        }

        var localUrl = LocalUrl.Trim();
        if (localUrl.Length > 0
            && !(Uri.TryCreate(localUrl, UriKind.Absolute, out var local) && (local.Scheme == Uri.UriSchemeHttp || local.Scheme == Uri.UriSchemeHttps)))
        {
            return Loc.Get("Validation_LocalUrl");
        }

        var eventType = CommandEventType.Trim();
        return eventType.Length == 0 || eventType.Any(char.IsWhiteSpace) ? Loc.Get("Validation_EventType") : null;
    }

    private bool SetSetting<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (!SetProperty(ref field, value, propertyName))
        {
            return false;
        }

        _changed();
        return true;
    }
}
