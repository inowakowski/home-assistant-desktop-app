namespace HADA.Core.Abstractions;

/// <summary>The brightness of the computer's own screen, on Linux and macOS; Windows has a control of its own for it.</summary>
public interface IScreenBrightness
{
    /// <summary>The brightness, 0-100; <see langword="null"/> where there is no screen to dim, as on a desktop.</summary>
    int? ReadPercent();

    /// <summary>Sets it, 0-100. Returns <see langword="null"/> when done, otherwise why it was not.</summary>
    string? SetPercent(int percent);
}

/// <summary>Whether the screens are on, and turning them off and on again, on Linux and macOS; Windows has its own.</summary>
public interface IDisplayPower
{
    /// <summary>Whether the screens are on; <see langword="null"/> where the system does not tell, and the sensor is left out.</summary>
    bool? ReadDisplayOn();

    /// <summary>Whether the screens can be turned off and on here; without, the buttons are left out.</summary>
    bool CanSwitch { get; }

    /// <summary>Turns the screens off. Returns <see langword="null"/> when done, otherwise why it was not.</summary>
    string? TurnOff();

    /// <summary>Turns the screens on again, as moving the mouse would. Returns <see langword="null"/> when done, otherwise why it was not.</summary>
    string? Wake();
}
