using HADA.Tray.Localization;
using HADA.Tray.Mvvm;

namespace HADA.Tray.ViewModels;

public sealed record LanguageOption(string Code, string Label);

/// <param name="Path">The browser's program file; empty for whichever browser is the default.</param>
public sealed record BrowserOption(string Path, string Label);

/// <summary>
/// The Settings page's personal part: choices of the signed-in user, applied the moment they are made and
/// without administrator rights.
/// </summary>
/// <param name="isElevated">
/// Whether this is the administrator copy of the window. It may run under another account, where these switches
/// would change that account's choices instead of the user's, so they are read-only there.
/// </param>
/// <param name="isPortable">Whether this is a portable copy, where starting with Windows means something else.</param>
public sealed class PreferencesViewModel(bool isElevated, bool isPortable = false) : ObservableObject
{
    private readonly string _languageAtStart = UserPreferences.Language;

    public bool CanChange { get; } = !isElevated;

    public string Note { get; } = Loc.Get(isElevated ? "Preferences_PersonalElevated" : "Preferences_PersonalDetail");

    public string AutostartDetail { get; } = Loc.Get(isPortable ? "Autostart_DetailPortable" : "Autostart_Detail");

    public IReadOnlyList<LanguageOption> Languages { get; } =
    [
        new(string.Empty, Loc.Get("Preferences_LanguageAuto")),
        new("pl", "Polski"),
        new("en", "English"),
    ];

    /// <summary>Whichever browser is the default, and then the installed ones by name.</summary>
    public IReadOnlyList<BrowserOption> Browsers { get; } =
    [
        new(string.Empty, Loc.Get("Preferences_BrowserDefault")),
        .. HADA.Tray.Browsers.Installed().Select(browser => new BrowserOption(browser.Path, browser.Name)),
    ];

    /// <summary>The program file of the browser that addresses of notifications are opened in; empty for the default one.</summary>
    public string NotificationBrowser
    {
        get => Browsers.Any(browser => string.Equals(browser.Path, UserPreferences.NotificationBrowser, StringComparison.OrdinalIgnoreCase))
            ? UserPreferences.NotificationBrowser
            : string.Empty;
        set
        {
            UserPreferences.NotificationBrowser = value ?? string.Empty;
            OnPropertyChanged();
        }
    }

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

    /// <summary>Kept only when it is an http or https address; anything else clears it.</summary>
    public string DashboardUrl
    {
        get => UserPreferences.DashboardUrl;
        set
        {
            UserPreferences.DashboardUrl = value ?? string.Empty;
            OnPropertyChanged();
        }
    }

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
