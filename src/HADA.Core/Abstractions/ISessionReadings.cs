namespace HADA.Core.Abstractions;

/// <summary>
/// What the sensors of the user's session read from the operating system: the tray app's on Windows, the app's
/// agent on Linux and macOS. The sensors are the same everywhere; each system has its own way of reading these.
/// </summary>
public interface ISessionReadings
{
    /// <summary>Time since the last keyboard or mouse input in this session; <see langword="null"/> where it cannot be read.</summary>
    TimeSpan? ReadIdleTime();

    /// <summary>
    /// The apps recording from <paramref name="device"/> now, by name; empty when none is. <see langword="null"/>
    /// where it cannot be told, and the sensor is left out.
    /// </summary>
    IReadOnlyList<string>? AppsUsing(CaptureDevice device);
}

/// <summary>What records the user: the microphone, the camera.</summary>
public enum CaptureDevice
{
    Microphone,
    Camera,
}
