using System.Windows;
using HADA.Tray.ViewModels;
using HADA.Tray.Views.Pages;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace HADA.Tray.Views;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _viewModel;
    private readonly Type _initialPage;

    /// <param name="initialPage">Page to open first; the overview when <see langword="null"/>.</param>
    public MainWindow(MainViewModel viewModel, Type? initialPage = null)
    {
        _viewModel = viewModel;
        _initialPage = initialPage ?? typeof(OverviewPage);
        CurrentPage = _initialPage;
        DataContext = viewModel;
        InitializeComponent();
        Navigation.SetPageProviderService(new PageProvider(viewModel, page => CurrentPage = page));
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    /// <summary>The page shown last, so an elevated copy of the window can reopen on the same page.</summary>
    public Type CurrentPage { get; private set; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // The watcher only reports changes made while a window is open, so catch up first: the system theme or
        // accent colour may have changed since the tray started or the window was last closed.
        ApplicationThemeManager.ApplySystemTheme(true);
        SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, true);
        Navigation.Navigate(_initialPage, _viewModel);
        await _viewModel.StartAsync();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);

        // Here and not in Closed: by then the window handle is gone, and UnWatch throws without one.
        if (!e.Cancel)
        {
            SystemThemeWatcher.UnWatch(this);
        }
    }

    private async void OnClosed(object? sender, EventArgs e) => await _viewModel.DisposeAsync();

    /// <summary>Creates each page once, all sharing the window's view model, and reports which one is showing.</summary>
    private sealed class PageProvider(object dataContext, Action<Type> onShown) : INavigationViewPageProvider
    {
        private readonly Dictionary<Type, FrameworkElement> _pages = [];

        public object GetPage(Type pageType)
        {
            if (!_pages.TryGetValue(pageType, out var page))
            {
                page = (FrameworkElement)Activator.CreateInstance(pageType)!;
                page.DataContext = dataContext;
                page.Loaded += (_, _) => onShown(pageType);
                _pages[pageType] = page;
            }

            return page;
        }
    }
}

/// <summary>Page names accepted by <c>--page</c>.</summary>
internal static class PageNames
{
    private static readonly Dictionary<string, Type> Pages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["overview"] = typeof(OverviewPage),
        ["connections"] = typeof(ConnectionsPage),
        ["entities"] = typeof(EntitiesPage),
        ["custom"] = typeof(CustomSensorsPage),
        ["logs"] = typeof(LogsPage),
    };

    public static Type? Find(string? name) => name is not null && Pages.TryGetValue(name, out var page) ? page : null;

    public static string NameOf(Type page) => Pages.FirstOrDefault(entry => entry.Value == page).Key ?? "overview";
}
