namespace HADA.Core.Abstractions;

public enum PowerCommand
{
    Sleep,
    Hibernate,
    Shutdown,
    Restart,
}

/// <summary>Carries out a <see cref="PowerCommand"/>; replaced in tests, which must not switch the computer off.</summary>
public interface IPowerControl
{
    /// <summary>Returns <see langword="null"/> when the command was accepted, otherwise why it was not.</summary>
    string? Execute(PowerCommand command);

    /// <summary>Whether the system can do it at all; a Mac, for one, cannot be hibernated on demand. Its button is then left out.</summary>
    bool Supports(PowerCommand command) => true;
}

/// <summary>
/// Keeps the computer from going to sleep by itself, for as long as it is held. Sleeping on purpose, from the menu or
/// with the lid, still works, and so does the screen turning off.
/// </summary>
public interface IKeepAwake
{
    /// <summary>Holds sleep off. Returns <see langword="null"/> when it is held, otherwise why not.</summary>
    string? Hold();

    /// <summary>Lets the computer sleep by itself again.</summary>
    void Release();
}

/// <summary>Locks the session at the computer's own screen.</summary>
public interface IScreenLock
{
    /// <summary>Returns <see langword="null"/> when the screen was locked, otherwise why it was not.</summary>
    string? Lock();
}
