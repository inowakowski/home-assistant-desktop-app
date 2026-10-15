using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
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
using HADA.Ipc;
using HADA.Platform.Windows.Desktop;
using HADA.Presentation.ViewModels;

namespace HADA.App;

public sealed partial class App : Application
{
    private const string SettingsArgument = "--settings";
    private const string PageArgument = "--page";

    /// <summary><c>--background</c>: on Linux and macOS, the agent alone, without the window, as when started at sign-in.</summary>
    private const string BackgroundArgument = "--background";

    /// <summary><c>--theme light|dark</c>: one of the two whatever the system uses, for trying it out.</summary>
    private const string ThemeArgument = "--theme";

    /// <summary><c>--snapshot file.png</c>: draws the window into the file a few seconds after it opened, and closes it.</summary>
    private const string SnapshotArgument = "--snapshot";

    private static readonly TimeSpan SnapshotDelay = TimeSpan.FromSeconds(5);

    private readonly string[] _args = Environment.GetCommandLineArgs().Skip(1).ToArray();
    private DesktopStyle _style;
    private Agent? _agent;
    private MainWindow? _window;
    private PosixSignalRegistration? _terminate;
    private bool _exiting;

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

            // The settings of a portable copy are its user's own, and so they are on Linux and macOS, where the
            // service is the user's; there is no administrator window to unlock.
            var usersOwn = AppInstance.IsPortable || !OperatingSystem.IsWindows();
            var isElevated = Program.IsElevated && !usersOwn;
            var client = new ServiceControlClient(new IpcOptions { ClientName = $"ui:{Environment.UserName}" });
            var viewModel = new MainViewModel(
                client, new AppDesktop(window), isElevated, usersOwn, () => RequestElevation(window.CurrentPage));

            // Let go of the service when the window closes. Not waited for on the window's thread, which the
            // view model's own continuations need: the process ends with the window either way.
            window.Closed += (_, _) => _ = viewModel.DisposeAsync().AsTask();

            // With an agent, closing the window only hides it: the agent goes on in the system tray.
            var withAgent = Agent.IsWanted && snapshot is null;
            if (withAgent)
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                window.Closing += (_, e) =>
                {
                    if (!_exiting)
                    {
                        e.Cancel = true;
                        window.Hide();
                    }
                };
                _ = StartAgentAsync(desktop, window);
            }

            // Started again, this window comes to the front. Closed when the tray app of this copy exits, which would
            // leave the program's files in use otherwise; with an agent, that ends the app.
            Program.Role?.Serve(
                () => Dispatcher.UIThread.Post(window.BringToFront),
                () => Dispatcher.UIThread.Post(withAgent ? () => Exit(desktop) : window.Close));
            if (snapshot is not null)
            {
                window.Opened += async (_, _) =>
                {
                    await Task.Delay(SnapshotDelay);
                    window.SaveSnapshot(snapshot);
                    window.Close();
                };
            }

            var hidden = withAgent && _args.Contains(BackgroundArgument);
            window.Show(viewModel, page, visible: !hidden);

            // The desktop's lifetime shows its main window as it starts; one started in the background has none yet.
            _window = window;
            if (!hidden)
            {
                desktop.MainWindow = window;
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task StartAgentAsync(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window)
    {
        // Asked to end, as when the user signs out: as tidily as from the icon's menu, the service told to stop.
        _terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            Dispatcher.UIThread.Post(() => Exit(desktop));
        });

        _agent = await Agent.StartAsync(
            this,
            () => Dispatcher.UIThread.Post(window.BringToFront),
            () => Dispatcher.UIThread.Post(() => Exit(desktop)));
    }

    /// <summary>Ends the app, its agent and the service the agent started.</summary>
    private async void Exit(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        try
        {
            if (_agent is { } agent)
            {
                await agent.DisposeAsync();
            }
        }
        finally
        {
            _terminate?.Dispose();
            _window?.Close();
            desktop.Shutdown();
        }
    }

    /// <summary>What follows an argument such as <c>--page</c>: <c>--page logs</c> gives <c>logs</c>.</summary>
    private string? ValueOf(string argument)
    {
        var index = Array.IndexOf(_args, argument);
        return index >= 0 && index + 1 < _args.Length ? _args[index + 1] : null;
    }

    /// <summary>Starts the window again as administrator, where the system has such a thing, and closes this one.</summary>
    private bool RequestElevation(string page)
    {
        if (!OperatingSystem.IsWindows() || !Elevation.TryRelaunchElevated($"{SettingsArgument} {PageArgument} {page}"))
        {
            return false;
        }

        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        return true;
    }
}
