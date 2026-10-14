using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using HADA.Core;
using HADA.Presentation.Localization;
using HADA.Tray.Localization;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace HADA.Tray.Views;

/// <summary>
/// A small window showing a Home Assistant dashboard, opened from the tray icon's menu: that of a Home Assistant
/// connected to directly, or the address the user chose on the Settings page. There is one window; choosing
/// another dashboard has it show that one. It is a browser of its own, with its own sign-in, kept in the user's
/// profile; closing the window ends its process, as with the settings window.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "WPF owns the window; the browser is released when the window closes.")]
public sealed class DashboardWindow : Window
{
    private const double Gap = 12;

    private readonly WebView2 _browser = new();
    private Uri _address;
    private readonly TaskCompletionSource<bool> _firstNavigation = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <param name="name">Whose dashboard it is, for the title; <see langword="null"/> when there is only one.</param>
    public DashboardWindow(Uri address, string? name)
    {
        _address = address;
        Title = TitleFor(name);
        Icon = BitmapFrame.Create(AppIcon.Uri);
        Width = 480;
        Height = 760;
        MinWidth = 320;
        MinHeight = 400;
        Content = _browser;

        // Near the notification area, where the click came from.
        var area = SystemParameters.WorkArea;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = area.Right - Width - Gap;
        Top = area.Bottom - Height - Gap;
        Loaded += OnLoaded;
        Closed += (_, _) => _browser.Dispose();
    }

    /// <summary>Shows another dashboard in this window; one it shows already is left as it is, not loaded anew.</summary>
    public void ShowDashboard(Uri address, string? name)
    {
        Title = TitleFor(name);
        if (address == _address)
        {
            return;
        }

        _address = address;

        // Before the browser is ready, the address is the one it starts with.
        _browser.CoreWebView2?.Navigate(address.AbsoluteUri);
    }

    private static string TitleFor(string? name) =>
        string.IsNullOrEmpty(name) ? Loc.Get("Dashboard_Title") : Loc.Format("Dashboard_TitleNamed", name);

    /// <summary>Completes when the address was loaded, or failed to: <see langword="true"/> when the page came up.</summary>
    public Task<bool> FirstNavigation => _firstNavigation.Task;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: Path.Combine(AppFolders.UserData, "dashboard"));
            await _browser.EnsureCoreWebView2Async(environment);
            _browser.CoreWebView2.NavigationCompleted += (_, args) => _firstNavigation.TrySetResult(args.IsSuccess);
            _browser.CoreWebView2.Navigate(_address.AbsoluteUri);
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or InvalidOperationException or IOException
            or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            // Windows 11 brings the WebView2 runtime along; on Windows 10 it may be missing.
            System.Windows.MessageBox.Show(
                Loc.Format("Dashboard_Failed", ex.Message), Loc.Get("Dashboard_Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            _firstNavigation.TrySetResult(false);
            Close();
        }
    }
}
