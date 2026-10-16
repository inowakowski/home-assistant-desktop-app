namespace HADA.Core.Abstractions;

/// <summary>"Do not disturb": whether the desktop keeps notifications quiet, and making it so.</summary>
public interface IDoNotDisturb
{
    /// <summary>Whether notifications are kept quiet now; <see langword="null"/> where the system does not say.</summary>
    bool? Read();

    /// <summary>Whether it can be switched here; without, the switch is left out.</summary>
    bool CanSet { get; }

    /// <summary>Turns it on or off. Returns <see langword="null"/> when done, otherwise why not.</summary>
    string? Set(bool on);
}
