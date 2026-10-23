namespace HADA.Presentation;

/// <summary>
/// The icons view models ask for. Named after the Fluent UI System Icons, which each app maps to the icon of its
/// own toolkit.
/// </summary>
public enum Glyph
{
    Alert,
    AlertOff,
    ArrowClockwise,
    ArrowDownload,
    ArrowUpload,
    ArrowSync,
    Battery5,
    BatteryCharge,
    BrightnessHigh,
    Circle,
    Clock,
    Database,
    Desktop,
    DesktopMac,
    DrinkCoffee,
    DeveloperBoard,
    Flash,
    Globe,
    Headphones,
    HomeCheckmark,
    Laptop,
    LockClosed,
    Mic,
    MicOff,
    MusicNote2,
    Next,
    NumberSymbol,
    Person,
    PersonCircle,
    Play,
    PlayCircle,
    PlugConnected,
    Power,
    Previous,
    Pulse,
    Router,
    Server,
    Speaker2,
    SpeakerMute,
    Stop,
    Storage,
    Timer,
    ToggleLeft,
    Video,
    WeatherMoon,
    Wifi1,
    Window,
    WindowApps,
}

/// <summary>How a message shown in the window is to be taken.</summary>
public enum Severity
{
    Informational,
    Success,
    Warning,
    Error,
}
