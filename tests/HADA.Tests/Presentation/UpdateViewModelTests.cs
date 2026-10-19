using System.Runtime.InteropServices;
using HADA.Core;
using HADA.Ipc;
using HADA.Presentation;
using HADA.Presentation.Localization;
using HADA.Presentation.ViewModels;

namespace HADA.Tests.Presentation;

/// <summary>What the window offers for a newer version: the Windows installer on Windows, the release page elsewhere.</summary>
public sealed class UpdateViewModelTests : IAsyncLifetime
{
    private const string ReleasePage = "https://github.com/inowakowski/home-assistant-desktop-app/releases/tag/v9.9.9";

    private readonly ServiceControlClient _client = new();
    private readonly RecordingDesktop _desktop = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _client.DisposeAsync();

    [Theory]
    [InlineData("Windows", false, false, true)]
    [InlineData("Windows", true, false, false)]
    [InlineData("Windows", false, true, false)]
    [InlineData("Linux", false, false, false)]
    [InlineData("Linux", false, true, false)]
    [InlineData("OSX", false, false, false)]
    [InlineData("OSX", false, true, false)]
    public void Only_the_ordinary_window_on_Windows_installs(string system, bool isElevated, bool isPortable, bool expected)
    {
        var updates = Create(system, isElevated, isPortable);
        updates.Update(StatusWithUpdate());

        Assert.Equal(expected, updates.CanInstall);
        Assert.Equal(expected, updates.InstallCommand.CanExecute(null));
    }

    [Fact]
    public void The_system_the_process_runs_on_is_the_default()
    {
        var updates = new UpdateViewModel(_client, _desktop, isElevated: false);

        Assert.Equal(OperatingSystem.IsWindows(), updates.CanInstall);
    }

    [Theory]
    [InlineData("Linux", "Update_DetailLinux")]
    [InlineData("OSX", "Update_DetailMac")]
    public void Elsewhere_the_window_says_to_install_the_package_by_hand(string system, string key)
    {
        // Linux and macOS are given isPortable, as their settings are the user's own; that is no portable copy.
        var updates = Create(system, isElevated: false, isPortable: true);

        Assert.Equal(Loc.Get(key), updates.Detail);
        Assert.NotEqual(Loc.Get("Update_DetailPortable"), updates.Detail);
        Assert.DoesNotContain("[", updates.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false, "Update_Detail")]
    [InlineData(true, false, "Update_DetailElevated")]
    [InlineData(false, true, "Update_DetailPortable")]
    public void On_Windows_the_window_says_what_the_installer_does(bool isElevated, bool isPortable, string key)
    {
        var updates = Create("Windows", isElevated, isPortable);

        Assert.Equal(Loc.Get(key), updates.Detail);
    }

    [Theory]
    [InlineData("Windows")]
    [InlineData("Linux")]
    [InlineData("OSX")]
    public void The_release_page_opens_on_every_system(string system)
    {
        var updates = Create(system, isElevated: false, isPortable: false);
        updates.Update(StatusWithUpdate());

        updates.OpenReleasePageCommand.Execute(null);

        Assert.Equal([new Uri(ReleasePage)], _desktop.Opened);
    }

    [Theory]
    [InlineData("Linux")]
    [InlineData("OSX")]
    public void Nothing_is_downloaded_or_started_elsewhere(string system)
    {
        var updates = Create(system, isElevated: false, isPortable: true);
        updates.Update(StatusWithUpdate());

        updates.InstallCommand.Execute(null);

        Assert.False(updates.IsDownloading);
        Assert.Empty(_desktop.Started);
        Assert.Equal(Loc.Get(system == "OSX" ? "Update_DetailMac" : "Update_DetailLinux"), updates.Detail);
    }

    private UpdateViewModel Create(string system, bool isElevated, bool isPortable) =>
        new(_client, _desktop, isElevated, isPortable, OSPlatform.Create(system));

    private static ServiceStatus StatusWithUpdate() =>
        new("1.13.0", DateTimeOffset.UtcNow, [], [], [], new UpdateInfo("9.9.9", ReleasePage));

    private sealed class RecordingDesktop : IDesktop
    {
        public List<Uri> Opened { get; } = [];

        public List<string> Started { get; } = [];

        public bool StartsWithSystem { get; set; }

        public void CopyText(string text)
        {
        }

        public void OpenInBrowser(Uri address) => Opened.Add(address);

        public void StartInstaller(string path) => Started.Add(path);

        public IReadOnlyList<InstalledBrowser> InstalledBrowsers() => [];

        public IReadOnlyList<UsbDeviceOption> ConnectedUsbDevices() => [];

        public IReadOnlyList<string> ConnectedNetworks() => [];
    }
}
