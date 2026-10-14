using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using HADA.App.Views;
using HADA.Core;
using HADA.Ipc;
using HADA.Platform.Windows.Desktop;
using HADA.Presentation.ViewModels;

namespace HADA.App;

public sealed partial class App : Application
{
    private const string SettingsArgument = "--settings";
    private const string PageArgument = "--page";

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var page = PageFrom(desktop.Args ?? []);
            var window = new MainWindow();

            // The settings of a portable copy are its user's own; there is no administrator window to unlock.
            var isElevated = OperatingSystem.IsWindows() && Elevation.IsElevated && !AppInstance.IsPortable;
            var client = new ServiceControlClient(new IpcOptions { ClientName = $"ui:{Environment.UserName}" });
            var viewModel = new MainViewModel(client, new AppDesktop(window), isElevated, AppInstance.IsPortable, () => RequestElevation(page));

            // Let go of the service when the window closes. Not waited for on the window's thread, which the
            // view model's own continuations need: the process ends with the window either way.
            window.Closed += (_, _) => _ = viewModel.DisposeAsync().AsTask();
            window.Show(viewModel, page);
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>The page asked for with <c>--page</c>, e.g. <c>--page logs</c>.</summary>
    private static string? PageFrom(string[] args)
    {
        var index = Array.IndexOf(args, PageArgument);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    /// <summary>Starts the window again as administrator, where the system has such a thing, and closes this one.</summary>
    private bool RequestElevation(string? page)
    {
        if (!OperatingSystem.IsWindows() || !Elevation.TryRelaunchElevated($"{SettingsArgument} {PageArgument} {page ?? "overview"}"))
        {
            return false;
        }

        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        return true;
    }
}
