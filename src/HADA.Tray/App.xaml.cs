using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Messaging;
using HADA.Ipc;
using HADA.Platform.Windows.Sensors;
using HADA.Tray.Localization;
using HADA.Tray.ViewModels;
using HADA.Tray.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wpf.Ui.Appearance;

namespace HADA.Tray;

/// <summary>
/// Runs in the user's session: hosts the session sensors (active window, volume), streams them to the service over the
/// IPC pipe, and offers the status and settings window from the notification-area icon.
/// </summary>
/// <remarks>
/// <c>--background</c> starts without opening the window (use it for sign-in startup).
/// <c>--settings</c> opens only the window; it is used when relaunching elevated to edit settings.
/// <c>--page overview|connections|entities|logs</c> chooses the page the window opens on.
/// </remarks>
public partial class App : Application
{
    private const string BackgroundArgument = "--background";
    private const string SettingsArgument = "--settings";
    private const string PageArgument = "--page";
    private const string ShowWindowSignalName = @"Local\HADA.Tray.ShowWindow";

    private Mutex? _singleInstance;
    private EventWaitHandle? _showWindowSignal;
    private RegisteredWaitHandle? _showWindowRegistration;
    private IHost? _host;
    private TrayIcon? _trayIcon;
    private MainWindow? _mainWindow;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ApplyLanguage();
        ApplicationThemeManager.ApplySystemTheme(true);
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        var initialPage = PageNames.Find(GetArgumentValue(e, PageArgument));

        if (HasArgument(e, SettingsArgument))
        {
            // Elevated copy started from "Unlock editing": just the window, and exit when it closes.
            ShowMainWindow(exitOnClose: true, initialPage);
            return;
        }

        _singleInstance = new Mutex(initiallyOwned: true, @"Local\HADA.Tray", out var isFirstInstance);
        if (!isFirstInstance)
        {
            // Already running in this session: bring up its window instead of starting a second tray.
            if (EventWaitHandle.TryOpenExisting(ShowWindowSignalName, out var signal))
            {
                using (signal)
                {
                    signal.Set();
                }
            }

            Shutdown();
            return;
        }

        _showWindowSignal = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, ShowWindowSignalName);
        _showWindowRegistration = ThreadPool.RegisterWaitForSingleObject(
            _showWindowSignal,
            (_, _) => Dispatcher.InvokeAsync(() => ShowMainWindow(exitOnClose: false)),
            state: null,
            Timeout.Infinite,
            executeOnlyOnce: false);

        var builder = Host.CreateApplicationBuilder(e.Args);
        builder.Services.AddSingleton<IEventBus, ChannelEventBus>();
        builder.Services.AddSingleton<IEntityRegistry, EntityRegistry>();
        builder.Services.Configure<IpcOptions>(options => options.ClientName = $"tray:{Environment.UserName}");

        // The IPC client starts first so it is subscribed before the sensors publish anything.
        builder.Services.AddSingleton<IpcClient>();
        builder.Services.AddHostedService(services => services.GetRequiredService<IpcClient>());
        builder.Services.AddHostedService<ActiveWindowSensor>();
        builder.Services.AddHostedService<AudioVolumeSensor>();
        _host = builder.Build();

        // Started on the thread pool so hosted services never capture the dispatcher's synchronization context,
        // which would also make the blocking stop in OnExit deadlock.
        await Task.Run(() => _host.StartAsync());

        var ipc = _host.Services.GetRequiredService<IpcClient>();
        _trayIcon = new TrayIcon(() => ipc.IsConnected, () => ShowMainWindow(exitOnClose: false), () => Shutdown());
        _host.Services.GetRequiredService<IHostApplicationLifetime>()
            .ApplicationStopping.Register(() => Dispatcher.InvokeAsync(() => Shutdown()));

        if (!HasArgument(e, BackgroundArgument))
        {
            ShowMainWindow(exitOnClose: false, initialPage);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showWindowRegistration?.Unregister(null);
        _showWindowSignal?.Dispose();
        _trayIcon?.Dispose();
        if (_host is not null)
        {
            _host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            _host.Dispose();
        }

        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void ShowMainWindow(bool exitOnClose, Type? initialPage = null)
    {
        if (_mainWindow is not null)
        {
            if (_mainWindow.WindowState == WindowState.Minimized)
            {
                _mainWindow.WindowState = WindowState.Normal;
            }

            _mainWindow.Activate();
            return;
        }

        var client = new ServiceControlClient(new IpcOptions { ClientName = $"ui:{Environment.UserName}" });
        var viewModel = new MainViewModel(client, Elevation.IsElevated, RequestElevation);
        _mainWindow = new MainWindow(viewModel, initialPage);
        _mainWindow.Closed += (_, _) =>
        {
            _mainWindow = null;
            if (exitOnClose)
            {
                Shutdown();
            }
        };
        _mainWindow.Show();
        _mainWindow.Activate();
    }

    private bool RequestElevation()
    {
        var page = _mainWindow is null ? "overview" : PageNames.NameOf(_mainWindow.CurrentPage);
        if (!Elevation.TryRelaunchElevated($"{SettingsArgument} {PageArgument} {page}"))
        {
            return false;
        }

        // The elevated copy takes over editing; this window would only show stale values.
        _mainWindow?.Close();
        return true;
    }

    private static bool HasArgument(StartupEventArgs e, string argument) =>
        e.Args.Contains(argument, StringComparer.OrdinalIgnoreCase);

    private static string? GetArgumentValue(StartupEventArgs e, string argument)
    {
        var index = Array.FindIndex(e.Args, candidate => string.Equals(candidate, argument, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < e.Args.Length ? e.Args[index + 1] : null;
    }

    private static void ApplyLanguage()
    {
        CultureInfo.DefaultThreadCurrentUICulture = Loc.Culture;
        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(Loc.Culture.IetfLanguageTag)));
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the tray and its sensors alive; report the problem instead of crashing.
        System.Windows.MessageBox.Show(e.Exception.Message, Loc.Get("Error_UnexpectedTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
