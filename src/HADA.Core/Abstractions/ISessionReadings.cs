namespace HADA.Core.Abstractions;

/// <summary>
/// What the sensors of the user's session read from the operating system: the tray app's on Windows, the app's
/// agent on Linux and macOS. The sensors are the same everywhere; each system has its own way of reading these.
/// </summary>
public interface ISessionReadings
{
    /// <summary>Time since the last keyboard or mouse input in this session; <see langword="null"/> where it cannot be read.</summary>
    TimeSpan? ReadIdleTime();
}
