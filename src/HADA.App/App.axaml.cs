using System.Net.Sockets;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.Themes.Fluent;
using HADA.App.Styling;
using HADA.App.Views;
using HADA.Core;
using HADA.Core.Hosting;
using HADA.Ipc;
using HADA.Platform.Windows.Desktop;
using HADA.Presentation.ViewModels;

namespace HADA.App;

public sealed partial class App : Application
{
    private const string SettingsArgument = "--settings";
    private const string PageArgument = "--page";

    /// <summary><c>--theme light|dark</c>: one of the two whatever the system uses, for trying it out.</summary>
    private const string ThemeArgument = "--theme";

    /// <summary><c>--snapshot file.png</c>: draws the window into the file a few seconds after it opened, and closes it.</summary>
    private const string SnapshotArgument = "--snapshot";

    private static readonly TimeSpan SnapshotDelay = TimeSpan.FromSeconds(5);

    private readonly string[] _args = Environment.GetCommandLineArgs().Skip(1).ToArray();
    private DesktopStyle _style;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        _style = DesktopStyles.Choose(_args);
        Styles.Add(new StyleInclude(new Uri("avares://HADA.App/"))
        {
            Source = new Uri($"avares://HADA.App/Views/Themes/{_style}.axaml"),
        });

        // Each system's accent colour; on Windows and on a Mac the one the user chose there.
        var isNative = _style == DesktopStyles.OfThisSystem();
        Color? accent = _style switch
        {
            DesktopStyle.Gnome => Color.Parse("#3584E4"),
            DesktopStyle.Kde => Color.Parse("#3DAEE9"),
            DesktopStyle.MacOS when !isNative => Color.Parse("#0A84FF"),
            _ => null,
        };
        if (accent is { } color && Styles.OfType<FluentTheme>().FirstOrDefault() is { } fluent)
        {
            fluent.Palettes[ThemeVariant.Dark] = new ColorPaletteResources { Accent = color };
            fluent.Palettes[ThemeVariant.Light] = new ColorPaletteResources { Accent = color };
        }

        // On a Mac, the system's own font.
        if (_style == DesktopStyle.MacOS && isNative)
        {
            Resources["HadaFontFamily"] = FontFamily.Default;
        }

        RequestedThemeVariant = ValueOf(ThemeArgument) switch
        {
            "light" => ThemeVariant.Light,
            "dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var page = ValueOf(PageArgument);
            var snapshot = ValueOf(SnapshotArgument);
            var window = new MainWindow();

            // HADA draws the frame where the system leaves it to apps, where another system's style is shown,
            // and for a snapshot, which has only what HADA draws.
            window.ApplyStyle(
                _style, drawnFrame: _style == DesktopStyle.Gnome || _style != DesktopStyles.OfThisSystem() || snapshot is not null);

            // The settings of a portable copy are its user's own; there is no administrator window to unlock.
            var isElevated = OperatingSystem.IsWindows() && Elevation.IsElevated && !AppInstance.IsPortable;
            var client = new ServiceControlClient(new IpcOptions { ClientName = $"ui:{Environment.UserName}" });
            var viewModel = new MainViewModel(client, new AppDesktop(window), isElevated, AppInstance.IsPortable, () => RequestElevation(page));

            // Let go of the service when the window closes. Not waited for on the window's thread, which the
            // view model's own continuations need: the process ends with the window either way.
            window.Closed += (_, _) => _ = viewModel.DisposeAsync().AsTask();

            // Closed when the tray app of this copy exits, which would leave the program's files in use otherwise.
            var closeSignal = ListenForClose(window);
            window.Closed += (_, _) => closeSignal?.Dispose();
            if (snapshot is not null)
            {
                window.Opened += async (_, _) =>
                {
                    await Task.Delay(SnapshotDelay);
                    window.SaveSnapshot(snapshot);
                    window.Close();
                };
            }

            window.Show(viewModel, page);
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static AppSignal? ListenForClose(MainWindow window)
    {
        try
        {
            return AppSignal.Listen(AppInstance.NewWindowCloseSignalName, () => Dispatcher.UIThread.Post(window.Close), once: true);
        }
        catch (Exception ex) when (ex is IOException or SocketException or UnauthorizedAccessException)
        {
            // Another of these windows listens already; the first one closes.
            return null;
        }
    }

    /// <summary>What follows an argument such as <c>--page</c>: <c>--page logs</c> gives <c>logs</c>.</summary>
    private string? ValueOf(string argument)
    {
        var index = Array.IndexOf(_args, argument);
        return index >= 0 && index + 1 < _args.Length ? _args[index + 1] : null;
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
