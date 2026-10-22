using HADA.Core;
using HADA.Presentation;
using HADA.Presentation.Localization;
using HADA.Presentation.ViewModels;

namespace HADA.Tests.Presentation;

/// <summary>
/// Whether the personal settings on the Settings page can be changed: in the ordinary window always, in the
/// administrator window while it runs as the user signed in, whose settings they are.
/// </summary>
public sealed class PreferencesViewModelTests
{
    [Theory]
    [InlineData(false, true, true)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    public void Only_an_administrator_window_of_another_account_keeps_them_read_only(bool isElevated, bool runsAsSessionUser, bool expected)
    {
        var preferences = new PreferencesViewModel(new SessionDesktop(runsAsSessionUser), isElevated);

        Assert.Equal(expected, preferences.CanChange);
        Assert.Equal(Loc.Get(expected ? "Preferences_PersonalDetail" : "Preferences_PersonalElevated"), preferences.Note);
    }

    // Whether it can be told or not, the ordinary window is the user's own.
    [Fact]
    public void The_ordinary_window_does_not_ask_whose_it_is()
    {
        var desktop = new SessionDesktop(runsAsSessionUser: false);

        _ = new PreferencesViewModel(desktop, isElevated: false);

        Assert.Equal(0, desktop.Asked);
    }

    private sealed class SessionDesktop(bool runsAsSessionUser) : IDesktop
    {
        public int Asked { get; private set; }

        public bool StartsWithSystem { get; set; }

        public bool RunsAsSessionUser
        {
            get
            {
                Asked++;
                return runsAsSessionUser;
            }
        }

        public void CopyText(string text)
        {
        }

        public void OpenInBrowser(Uri address)
        {
        }

        public void StartInstaller(string path)
        {
        }

        public IReadOnlyList<InstalledBrowser> InstalledBrowsers() => [];

        public IReadOnlyList<UsbDeviceOption> ConnectedUsbDevices() => [];

        public IReadOnlyList<string> ConnectedNetworks() => [];
    }
}
