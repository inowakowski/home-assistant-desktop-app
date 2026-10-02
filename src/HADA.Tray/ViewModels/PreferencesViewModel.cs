using HADA.Tray.Localization;
using HADA.Tray.Mvvm;

namespace HADA.Tray.ViewModels;

public sealed record LanguageOption(string Code, string Label);

/// <summary>
/// The Settings page's personal part: choices of the signed-in user, applied the moment they are made and
/// without administrator rights.
/// </summary>
/// <param name="isElevated">
/// Whether this is the administrator copy of the window. It may run under another account, where these switches
/// would change that account's choices instead of the user's, so they are read-only there.
/// </param>
public sealed class PreferencesViewModel(bool isElevated) : ObservableObject
{
    private readonly string _languageAtStart = UserPreferences.Language;

    public bool CanChange { get; } = !isElevated;

    public string Note { get; } = Loc.Get(isElevated ? "Preferences_PersonalElevated" : "Preferences_PersonalDetail");

    public IReadOnlyList<LanguageOption> Languages { get; } =
    [
        new(string.Empty, Loc.Get("Preferences_LanguageAuto")),
        new("pl", "Polski"),
        new("en", "English"),
    ];

    public double MinIdleSeconds => UserPreferences.MinIdleSeconds;

    public double MaxIdleSeconds => UserPreferences.MaxIdleSeconds;

    public bool StartWithWindows
    {
        get => Autostart.IsEnabled;
        set
        {
            Autostart.IsEnabled = value;
            OnPropertyChanged();
        }
    }

    public string Language
    {
        get => UserPreferences.Language;
        set
        {
            UserPreferences.Language = value ?? string.Empty;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsLanguageChanged));
        }
    }

    /// <summary>The window keeps the language it was opened in; this says a reopening is due.</summary>
    public bool IsLanguageChanged => UserPreferences.Language != _languageAtStart;

    public double? IdleSeconds
    {
        get => UserPreferences.IdleSeconds;
        set
        {
            // An emptied box is on its way to a new number, not a wish for the default.
            if (value is { } seconds && !double.IsNaN(seconds))
            {
                UserPreferences.IdleSeconds = (int)Math.Round(seconds);
                OnPropertyChanged();
            }
        }
    }
}
