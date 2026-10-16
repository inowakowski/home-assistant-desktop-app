namespace HADA.Core.Abstractions;

/// <summary>How many screens are connected, and how many of them are not the laptop's own.</summary>
public readonly record struct DisplayCount(int Total, int External);

/// <summary>The window in front: its title, and the program it belongs to.</summary>
/// <param name="Title">Empty where the system does not tell apps the titles of others' windows, as macOS does not.</param>
public readonly record struct ActiveWindowInfo(string Title, string? ProcessName);

/// <summary>
/// What the service reads about the computer's hardware on Linux and macOS; Windows has sensors of its own for
/// these. Every method returns <see langword="null"/> where the value cannot be read.
/// </summary>
public interface IHardwareReadings
{
    /// <summary>Whether the lid is open; <see langword="null"/> where there is no lid to tell.</summary>
    bool? ReadLidOpen();

    /// <summary>Whether the system can tell how busy the graphics processor is; without, the sensor is left out.</summary>
    bool HasGpuLoad { get; }

    /// <summary>How busy the graphics processor is, 0-100; <see langword="null"/> where it cannot be told right now.</summary>
    int? ReadGpuLoadPercent();
}

/// <summary>What the sensors of the user's session read about the screens and the window in front, on Linux and macOS.</summary>
public interface IDisplayReadings
{
    /// <summary>The connected screens; <see langword="null"/> where they cannot be counted.</summary>
    DisplayCount? ReadDisplays();

    /// <summary>Whether the system tells apps which window is in front; Wayland does not, and the sensor is left out.</summary>
    bool HasActiveWindow { get; }

    /// <summary>The window in front; <see langword="null"/> while there is none, as on an empty desktop.</summary>
    ActiveWindowInfo? ReadActiveWindow();
}
