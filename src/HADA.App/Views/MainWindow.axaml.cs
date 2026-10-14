using Avalonia.Controls;
using HADA.App.Views.Pages;
using HADA.Presentation.Localization;
using HADA.Presentation.ViewModels;
using FluentIconKind = FluentIcons.Common.Icon;

namespace HADA.App.Views;

/// <summary>One entry of the navigation: the page it leads to, by the name <c>--page</c> and the view models use.</summary>
public sealed record NavigationItem(string Name, string Title, FluentIconKind Icon);

/// <summary>The settings window: navigation on the left, the chosen page on the right.</summary>
public sealed partial class MainWindow : Window
{
    private static readonly NavigationItem[] Main =
    [
        new("overview", Loc.Get("Nav_Overview"), FluentIconKind.Home),
        new("connections", Loc.Get("Nav_Connections"), FluentIconKind.PlugConnected),
        new("entities", Loc.Get("Nav_Entities"), FluentIconKind.AppsList),
        new("custom", Loc.Get("Nav_CustomSensors"), FluentIconKind.AddCircle),
    ];

    private static readonly NavigationItem[] Footer =
    [
        new("settings", Loc.Get("Nav_Settings"), FluentIconKind.Settings),
        new("logs", Loc.Get("Nav_Logs"), FluentIconKind.DocumentText),
    ];

    private bool _choosing;

    public MainWindow()
    {
        InitializeComponent();
        MainPages.ItemsSource = Main;
        FooterPages.ItemsSource = Footer;
    }

    /// <summary>Shows the window for <paramref name="viewModel"/>, at <paramref name="page"/> or the Overview.</summary>
    public void Show(MainViewModel viewModel, string? page)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        Title = viewModel.WindowTitle;
        viewModel.NavigationRequested += ShowPage;
        Opened += async (_, _) => await viewModel.StartAsync();
        ShowPage(page ?? "overview");
        Show();
    }

    /// <summary>
    /// Shows a page by its name; what follows a <c>#</c>, such as <c>connections#mqtt=</c> and a server's id, is
    /// for the page to act on.
    /// </summary>
    private void ShowPage(string target)
    {
        var name = target.Split('#', 2)[0];
        var item = Main.Concat(Footer).FirstOrDefault(candidate => candidate.Name == name) ?? Main[0];

        _choosing = true;
        MainPages.SelectedItem = Main.Contains(item) ? item : null;
        FooterPages.SelectedItem = Footer.Contains(item) ? item : null;
        _choosing = false;

        PageHost.Content = item.Name switch
        {
            "overview" => new OverviewPage(),
            _ => new PendingPage(),
        };
    }

    private void OnPageChosen(object? sender, SelectionChangedEventArgs e)
    {
        if (!_choosing && sender is ListBox { SelectedItem: NavigationItem item })
        {
            ShowPage(item.Name);
        }
    }
}
