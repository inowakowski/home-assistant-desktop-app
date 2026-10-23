namespace HADA.Core.Abstractions;

/// <summary>What fills the screen at the computer.</summary>
public enum FullScreenKind
{
    None,

    /// <summary>A window made full screen, such as a film in a browser.</summary>
    App,

    /// <summary>A game drawing to the screen itself.</summary>
    Game,

    /// <summary>A slide show, or the system's presentation mode.</summary>
    Presentation,
}

/// <summary>Whether an app fills the screen of the user's session: a game, a film, a slide show.</summary>
public interface IFullScreen
{
    /// <summary>What fills the screen now; <see langword="null"/> where it cannot be told, and the sensor is left out.</summary>
    FullScreenKind? Read();
}

/// <summary>The dark look of the desktop and its apps: whether it is on, and switching it.</summary>
public interface IDarkMode
{
    /// <summary>Whether it is on; <see langword="null"/> where the desktop does not say.</summary>
    bool? Read();

    /// <summary>Whether it can be switched here; without, the switch is left out.</summary>
    bool CanSet { get; }

    /// <summary>Turns it on or off. Returns <see langword="null"/> when done, otherwise why not.</summary>
    string? Set(bool dark);
}

/// <summary>Signs the user at the computer out, as the desktop's own menu does.</summary>
public interface ISignOut
{
    /// <summary>Whether this desktop can be told to; without, the button is left out.</summary>
    bool CanSignOut { get; }

    /// <summary>
    /// Asks the desktop to sign out. Apps with unsaved work may still hold it up. Returns <see langword="null"/> when
    /// asked, otherwise why not.
    /// </summary>
    string? SignOut();
}
