using HADA.Core.Input;

namespace HADA.Core.Abstractions;

/// <summary>Presses keys on the user's desktop, as if they were typed there.</summary>
public interface IKeyboard
{
    /// <summary>Presses the combination and lets go of it.</summary>
    /// <returns><see langword="null"/> when done; otherwise why it could not be, for the log.</returns>
    string? Press(KeyCombination combination);
}
