using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using HADA.Core.Logging;
using HADA.Core.Abstractions;
using HADA.Core.Models;
using HADA.Ipc;
using HADA.Tray.Localization;
using HADA.Tray.Session;
using HADA.Tray.ViewModels;
using HADA.Tray.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;

namespace HADA.Tray;

/// <summary>
/// Runs in the user's session, in one of two roles chosen by the command line.
/// As the tray it hosts the session sensors (active window, volume, …) and actions (volume, media keys,
/// notifications, …), connects them to the service over the IPC pipe and shows the notification-area icon. As the window (<c>--settings</c>) it is the status and settings window.
/// </summary>
/// <remarks>
/// The window is a process of its own on purpose. Showing any WPF window sets up the graphics pipeline, which costs
/// a few hundred megabytes that are never given back; in a separate process they go away when the window is closed,
/// and the tray, which runs all day, stays small.
/// <para>
/// <c>--background</c> starts the tray without opening the window (use it for sign-in startup).
/// <c>--autostart</c> marks a start made by Windows at sign-in; the tray then exits if the user turned autostart off.
/// <c>--settings</c> runs as the window, without tray icon or sensors.
/// <c>--dashboard</c> runs as the dashboard window, showing the address chosen on the Settings page.
/// <c>--page overview|connections|entities|custom|settings|logs</c> chooses the page the window opens on.
/// </para>
/// </remarks>
[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "WPF owns the application object; everything is released in OnExit.")]
public partial class App : Application
{
    private const string BackgroundArgument = "--background";
    private const string SettingsArgument = "--settings";
    private const string DashboardArgument = "--dashboard";
    private const string PageArgument = "--page";
    private const int AnyProcess = -1;

    private Mutex? _singleInstance;
    private EventWaitHandle? _showWindowSignal;
    private RegisteredWaitHandle? _showWindowRegistration;
    private FileLoggerProvider? _fileLog;
    private ILogger _logger = NullLogger.Instance;
    private IHost? _host;
    private TrayIcon? _trayIcon;
    private GlobalHotkeys? _hotkeys;
    private MainWindow? _mainWindow;
    private Window? _roleWindow;
    private bool _isShowingError;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // The windows run next to the tray, so each writes its own file.
        var isDashboard = HasArgument(e, DashboardArgument);
        var isWindow = HasArgument(e, SettingsArgument);
        _fileLog = new FileLoggerProvider(new FileLoggerOptions
        {
            FilePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HADA",
                "logs",
                isDashboard ? "dashboard-window.log" : isWindow ? "settings-window.log" : "tray.log"),
        });
        _logger = _fileLog.CreateLogger(typeof(App).FullName!);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogCrash(_logger, args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogBackgroundFailure(_logger, args.Exception);
            args.SetObserved();
        };

        try
        {
            ApplyLanguage();
            var page = GetArgumentValue(e, PageArgument);
            if (isDashboard)
            {
                StartDashboard();
            }
            else if (isWindow)
            {
                StartWindow(page);
            }
            else
            {
                await StartTrayAsync(e, page);
            }
        }
        catch (Exception ex)
        {
            // Without this, a failed start would leave an invisible process behind: no icon, no window, no sensors.
            LogStartFailed(_logger, ex);
            System.Windows.MessageBox.Show(ex.Message, Loc.Get("Error_UnexpectedTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showWindowRegistration?.Unregister(null);
        _showWindowSignal?.Dispose();
        _hotkeys?.Dispose();
        _trayIcon?.Dispose();
        if (_host is not null)
        {
            _host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            _host.Dispose();
        }

        _singleInstance?.Dispose();
        _fileLog?.Dispose();
        base.OnExit(e);
    }

    private async Task StartTrayAsync(StartupEventArgs e, string? page)
    {
        if (HasArgument(e, Autostart.Argument) && !Autostart.IsEnabled)
        {
            // Started by Windows at sign-in, but this user chose not to start HADA with Windows.
            Shutdown();
            return;
        }

        _singleInstance = new Mutex(initiallyOwned: true, @"Local\HADA.Tray", out var isFirstInstance);
        if (!isFirstInstance)
        {
            // The tray is already running in this session; starting HADA again means "show me the window".
            OpenWindow(page);
            Shutdown();
            return;
        }

        var builder = Host.CreateApplicationBuilder(e.Args);
        builder.Logging.AddProvider(_fileLog!);

        builder.Services.AddSessionServices();
        _host = builder.Build();

        // Started on the thread pool so hosted services never capture the dispatcher's synchronization context,
        // which would also make the blocking stop in OnExit deadlock.
        await Task.Run(() => _host.StartAsync());

        var ipc = _host.Services.GetRequiredService<IpcClient>();
        _trayIcon = new TrayIcon(
            () => ipc.IsConnected, () => OpenWindow(page: null), () => StartCopy(DashboardArgument), () => Shutdown());

        // Notifications: toasts, which can carry a picture and buttons; the icon's plain balloon when a toast fails.
        var bus = _host.Services.GetRequiredService<IEventBus>();
        var toasts = new ToastPresenter(
            action => _ = bus.PublishAsync(new DeviceEvent { Name = DeviceEvent.NotificationAction, Value = action }).AsTask(),
            (title, message) => Dispatcher.InvokeAsync(() => _trayIcon?.ShowNotification(title, message)),
            _logger);
        try
        {
            toasts.EnsureRegistered();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Tried again with the first notification, which falls back to the icon's balloon if it still fails.
            LogToastRegistrationFailed(_logger, ex.Message);
        }

        _host.Services.GetRequiredService<NotificationPresenter>().Show = request => _ = toasts.ShowAsync(request);

        // Quick actions: in the icon's menu, and under their shortcuts. Hotkeys belong to this, the message-pumping thread.
        _hotkeys = new GlobalHotkeys(_logger);
        var quickActions = _host.Services.GetRequiredService<QuickActions>();
        void ShowQuickActions(IReadOnlyList<QuickActionInfo> actions) => Dispatcher.InvokeAsync(() =>
        {
            _trayIcon?.SetQuickActions([.. actions.Select(action => (action.Name, action.Hotkey, (Action)(() => quickActions.Choose(action))))]);
            _hotkeys?.Set(actions.Select(action => (action.Hotkey, action.Name, (Action)(() => quickActions.Choose(action)))));
        });
        quickActions.Changed += ShowQuickActions;
        ShowQuickActions(quickActions.Current);

        _host.Services.GetRequiredService<IHostApplicationLifetime>()
            .ApplicationStopping.Register(() => Dispatcher.InvokeAsync(() => Shutdown()));

        if (!HasArgument(e, BackgroundArgument))
        {
            OpenWindow(page);
        }
    }

    /// <summary>Starts the window as its own process; if one is open already, that process brings it to the front instead.</summary>
    private void OpenWindow(string? page)
    {
        if (page is null)
        {
            StartCopy(SettingsArgument);
        }
        else
        {
            StartCopy(SettingsArgument, PageArgument, page);
        }
    }

    /// <summary>Starts this program again in another of its roles: the settings window or the dashboard window.</summary>
    private void StartCopy(params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            Process.Start(start)?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            LogWindowStartFailed(_logger, ex);
            System.Windows.MessageBox.Show(ex.Message, Loc.Get("Error_UnexpectedTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>The dashboard window: one per user; asked for again, the one that is open comes to the front.</summary>
    private void StartDashboard()
    {
        const string Name = @"Local\HADA.Dashboard";
        if (!UserPreferences.TryGetDashboardAddress(UserPreferences.DashboardUrl, out var address) || !TakeWindowRole(Name))
        {
            Shutdown();
            return;
        }

        var window = new DashboardWindow(address);
        _roleWindow = window;
        window.Closed += (_, _) => Shutdown();
        window.Show();
        window.Activate();
    }

    /// <summary>
    /// Makes this process the one showing a kind of window. Returns <see langword="false"/> when another process
    /// already is; that one is then asked to bring its window to the front.
    /// </summary>
    private bool TakeWindowRole(string name)
    {
        _singleInstance = new Mutex(initiallyOwned: true, name, out var isFirstInstance);
        if (!isFirstInstance)
        {
            if (EventWaitHandle.TryOpenExisting(name + ".Show", out var signal))
            {
                using (signal)
                {
                    // This process was started by the user and may take the foreground; pass that right on,
                    // or Windows would only flash the existing window's taskbar button.
                    AllowSetForegroundWindow(AnyProcess);
                    signal.Set();
                }
            }

            return false;
        }

        _showWindowSignal = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, name + ".Show");
        _showWindowRegistration = ThreadPool.RegisterWaitForSingleObject(
            _showWindowSignal,
            (_, _) => Dispatcher.InvokeAsync(BringWindowToFront),
            state: null,
            Timeout.Infinite,
            executeOnlyOnce: false);
        return true;
    }

    private void StartWindow(string? page)
    {
        // One window per user and privilege level: the administrator copy opened with "Unlock editing" may run
        // next to the normal one for a moment, while that one is closing.
        if (!TakeWindowRole(Elevation.IsElevated ? @"Local\HADA.Window.Admin" : @"Local\HADA.Window"))
        {
            Shutdown();
            return;
        }

        // Software rendering: on some graphics drivers, notably on ARM64, setting up hardware rendering alone
        // takes over 200 MB, and these pages have nothing a CPU cannot draw instantly.
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        // Loaded here rather than in App.xaml, so the tray never pays for the control library it does not use.
        Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = ApplicationTheme.Dark });
        Resources.MergedDictionaries.Add(new ControlsDictionary());
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/HADA.Tray;component/Views/Styles.xaml"),
        });
        ApplicationThemeManager.ApplySystemTheme(true);

        var client = new ServiceControlClient(new IpcOptions { ClientName = $"ui:{Environment.UserName}" });
        var viewModel = new MainViewModel(client, Elevation.IsElevated, RequestElevation);
        _mainWindow = new MainWindow(viewModel, PageNames.Find(page));
        _roleWindow = _mainWindow;
        _mainWindow.Closed += (_, _) => Shutdown();
        _mainWindow.Show();
        _mainWindow.Activate();
    }

    private void BringWindowToFront()
    {
        if (_roleWindow is null)
        {
            return;
        }

        if (_roleWindow.WindowState == WindowState.Minimized)
        {
            _roleWindow.WindowState = WindowState.Normal;
        }

        _roleWindow.Activate();
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
        e.Handled = true;
        LogUnhandled(_logger, e.Exception);

        // One box at a time: a message box pumps messages, so a fault that repeats would otherwise stack boxes
        // on top of each other until the process runs out of stack.
        if (_isShowingError)
        {
            return;
        }

        _isShowingError = true;
        try
        {
            System.Windows.MessageBox.Show(e.Exception.Message, Loc.Get("Error_UnexpectedTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isShowingError = false;
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notifications could not be registered with Windows: {Reason}")]
    private static partial void LogToastRegistrationFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Critical, Message = "HADA could not start.")]
    private static partial void LogStartFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "The HADA window could not be opened.")]
    private static partial void LogWindowStartFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled error in the user interface.")]
    private static partial void LogUnhandled(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "A background task failed without being observed.")]
    private static partial void LogBackgroundFailure(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Critical, Message = "HADA is crashing.")]
    private static partial void LogCrash(ILogger logger, Exception? exception);
}
