using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using HADA.App.Styling;
using HADA.App.Views.Pages;
using HADA.Presentation;
using HADA.Presentation.Localization;
using HADA.Presentation.ViewModels;
using FluentIconKind = FluentIcons.Common.Icon;

namespace HADA.App.Views;

/// <summary>One entry of the navigation: the page it leads to, by the name <c>--page</c> and the view models use.</summary>
/// <param name="Tint">The colour of its tile, in the styles that put the icons on coloured tiles.</param>
public sealed record NavigationItem(string Name, string Title, FluentIconKind Icon, IBrush Tint);

/// <summary>The settings window: this computer and the pages on the left, the chosen page on the right.</summary>
public sealed partial class MainWindow : Window
{
    private static readonly NavigationItem[] Main =
    [
        new("overview", Loc.Get("Nav_Overview"), FluentIconKind.Home, Brush.Parse("#0A84FF")),
        new("connections", Loc.Get("Nav_Connections"), FluentIconKind.PlugConnected, Brush.Parse("#30D158")),
        new("entities", Loc.Get("Nav_Entities"), FluentIconKind.AppsList, Brush.Parse("#FF9F0A")),
        new("custom", Loc.Get("Nav_CustomSensors"), FluentIconKind.AddCircle, Brush.Parse("#BF5AF2")),
    ];

    private static readonly NavigationItem[] Footer =
    [
        new("settings", Loc.Get("Nav_Settings"), FluentIconKind.Settings, Brush.Parse("#8E8E93")),
        new("logs", Loc.Get("Nav_Logs"), FluentIconKind.DocumentText, Brush.Parse("#5E5CE6")),
    ];

    private bool _choosing;
    private bool _showsSidebarTitle;
    private MainViewModel? _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        MainPages.ItemsSource = Main;
        FooterPages.ItemsSource = Footer;
        SetSidebarCollapsed(UserPreferences.SidebarCollapsed);
    }

    /// <summary>
    /// Dresses the window in a system's style. With <paramref name="drawnFrame"/> HADA draws the title bar and
    /// the window's buttons itself: always for GNOME, which leaves that to apps, and when showing a style on a
    /// system it is not from.
    /// </summary>
    public void ApplyStyle(DesktopStyle style, bool drawnFrame)
    {
        if (drawnFrame)
        {
            WindowDecorations = WindowDecorations.None;
            ResizeEdges.IsVisible = true;
        }

        switch (style)
        {
            case DesktopStyle.Windows:
                // A title bar across the window, the navigation and the page below it. The buttons are HADA's:
                // the ones Avalonia would draw add a button for full screen, which Windows apps do not have.
                // The bar itself is the system's to drag and double-click, and Mica shows through the window.
                TitleRow.IsVisible = true;
                ContentHeader.IsVisible = false;
                WindowsButtons.IsVisible = true;
                WindowDecorations = WindowDecorations.None;
                ResizeEdges.IsVisible = true;
                if (!drawnFrame)
                {
                    ExtendClientAreaToDecorationsHint = true;
                    ExtendClientAreaTitleBarHeightHint = TitleRow.Height;
                    TransparencyLevelHint = [WindowTransparencyLevel.Mica, WindowTransparencyLevel.AcrylicBlur];
                    Background = Brushes.Transparent;
                    Frame.Background = Brushes.Transparent;
                }

                break;

            case DesktopStyle.MacOS:
                // The traffic lights over the sidebar, the page's title in the toolbar.
                MacLights.IsVisible = drawnFrame;
                if (!drawnFrame)
                {
                    ExtendClientAreaToDecorationsHint = true;
                    ExtendClientAreaTitleBarHeightHint = SidebarHeader.Height;
                    TransparencyLevelHint = [WindowTransparencyLevel.AcrylicBlur];
                    Background = Brushes.Transparent;
                    Frame.Background = Brushes.Transparent;
                }

                break;

            case DesktopStyle.Gnome:
                // Header bars on both panes, the page's title centred, a close button, round corners.
                _showsSidebarTitle = true;
                SidebarTitle.IsVisible = !Sidebar.Classes.Contains("collapsed");
                SidebarTitle.HorizontalAlignment = HorizontalAlignment.Center;
                HeaderTitle.HorizontalAlignment = HorizontalAlignment.Center;
                HeaderTitle.Margin = new Thickness(56, 0, 20, 0);
                GnomeClose.IsVisible = true;
                Frame.CornerRadius = new CornerRadius(12);
                Frame.ClipToBounds = true;
                Background = Brushes.Transparent;
                TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
                break;

            case DesktopStyle.Kde:
                // KWin's title bar, or Breeze's drawn like it; a toolbar with the page's title above the page.
                _showsSidebarTitle = true;
                SidebarTitle.IsVisible = !Sidebar.Classes.Contains("collapsed");
                SidebarTitle.Margin = new Thickness(16, 0);
                SidebarHeader.Background = ContentHeader.Background;
                if (drawnFrame)
                {
                    TitleRow.IsVisible = true;
                    TitleRow.Height = 32;
                    TitleIdentity.HorizontalAlignment = HorizontalAlignment.Center;
                    KdeButtons.IsVisible = true;
                }

                break;
        }
    }

    /// <summary>Shows the window for <paramref name="viewModel"/>, at <paramref name="page"/> or the Overview.</summary>
    public void Show(MainViewModel viewModel, string? page)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        _viewModel = viewModel;
        Title = viewModel.WindowTitle;
        TitleText.Text = viewModel.WindowTitle;
        viewModel.NavigationRequested += ShowPage;
        Opened += async (_, _) => await viewModel.StartAsync();
        ShowPage(page ?? "overview");
        Show();
    }

    /// <summary>Draws the window into a PNG file, as it is now, for pictures of it.</summary>
    public void SaveSnapshot(string path)
    {
        var scaling = RenderScaling;
        var size = new PixelSize((int)(Bounds.Width * scaling), (int)(Bounds.Height * scaling));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scaling, 96 * scaling));
        bitmap.Render(this);
        bitmap.Save(path, new PngBitmapEncoderOptions());
    }

    /// <summary>
    /// Shows a page by its name; what follows a <c>#</c>, such as <c>connections#mqtt=</c> and a server's id, is
    /// for the page to act on.
    /// </summary>
    private void ShowPage(string target)
    {
        var parts = target.Split('#', 2);
        var name = parts[0];
        var item = Main.Concat(Footer).FirstOrDefault(candidate => candidate.Name == name) ?? Main[0];

        _choosing = true;
        MainPages.SelectedItem = Main.Contains(item) ? item : null;
        FooterPages.SelectedItem = Footer.Contains(item) ? item : null;
        _choosing = false;

        HeaderTitle.Text = item.Title;
        if (item.Name == "connections")
        {
            _viewModel?.Connections.Show(parts.Length > 1 ? parts[1] : null);
        }
        else
        {
            _viewModel?.Connections.Leave();
        }

        PageHost.Content = item.Name switch
        {
            "overview" => new OverviewPage(),
            "connections" => new ConnectionsPage(),
            _ => new PendingPage(),
        };
    }

    private void OnToggleSidebar(object? sender, RoutedEventArgs e)
    {
        var collapsed = !Sidebar.Classes.Contains("collapsed");
        SetSidebarCollapsed(collapsed);
        UserPreferences.SidebarCollapsed = collapsed;
    }

    /// <summary>Shows the sidebar with the names of the pages, or their icons only.</summary>
    private void SetSidebarCollapsed(bool collapsed)
    {
        Sidebar.Classes.Set("collapsed", collapsed);
        SidebarTitle.IsVisible = _showsSidebarTitle && !collapsed;
    }

    private void OnPageChosen(object? sender, SelectionChangedEventArgs e)
    {
        if (!_choosing && sender is ListBox { SelectedItem: NavigationItem item })
        {
            ShowPage(item.Name);
        }
    }

    private void OnDragWindow(object? sender, PointerPressedEventArgs e)
    {
        if (WindowDecorations == WindowDecorations.None && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount == 1)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnResize(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { Tag: string edge } && Enum.TryParse<WindowEdge>(edge, out var windowEdge))
        {
            BeginResizeDrag(windowEdge, e);
        }
    }

    private void OnToggleMaximize(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnCloseLight(object? sender, PointerPressedEventArgs e) => Close();

    private void OnMinimizeLight(object? sender, PointerPressedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeLight(object? sender, PointerPressedEventArgs e) => OnToggleMaximize(sender, e);
}
