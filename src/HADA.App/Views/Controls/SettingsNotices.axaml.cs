using Avalonia;
using Avalonia.Controls;
using HADA.Presentation.Localization;

namespace HADA.App.Views.Controls;

public sealed partial class SettingsNotices : UserControl
{
    /// <summary>What the notice says about unlocking; the Settings page has its own words for it.</summary>
    public static readonly StyledProperty<string> LockedTextProperty =
        AvaloniaProperty.Register<SettingsNotices, string>(nameof(LockedText), Loc.Get("Settings_LockedMessage"));

    public SettingsNotices() => InitializeComponent();

    public string LockedText
    {
        get => GetValue(LockedTextProperty);
        set => SetValue(LockedTextProperty, value);
    }
}
