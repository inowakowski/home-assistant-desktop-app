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
}

/// <summary>Locks the session at the computer's own screen.</summary>
public interface IScreenLock
{
    /// <summary>Returns <see langword="null"/> when the screen was locked, otherwise why it was not.</summary>
    string? Lock();
}
