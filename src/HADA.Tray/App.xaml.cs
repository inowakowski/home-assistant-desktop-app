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
using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Logging;
using HADA.Core.Messaging;
using HADA.Ipc;
using HADA.Platform.Windows.Sensors;
using HADA.Tray.Localization;
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
/// As the tray it hosts the session sensors (active window, volume, …), streams them to the service over the IPC
/// pipe and shows the notification-area icon. As the window (<c>--settings</c>) it is the status and settings window.
/// </summary>
/// <remarks>
/// The window is a process of its own on purpose. Showing any WPF window sets up the graphics pipeline, which costs
/// a few hundred megabytes that are never given back; in a separate process they go away when the window is closed,
/// and the tray, which runs all day, stays small.
/// <para>
/// <c>--background</c> starts the tray without opening the window (use it for sign-in startup).
/// <c>--autostart</c> marks a start made by Windows at sign-in; the tray then exits if the user turned autostart off.
/// <c>--settings</c> runs as the window, without tray icon or sensors.
/// <c>--page overview|connections|entities|custom|logs</c> chooses the page the window opens on.
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
    private const string PageArgument = "--page";
    private const int AnyProcess = -1;

    private Mutex? _singleInstance;
    private EventWaitHandle? _showWindowSignal;
    private RegisteredWaitHandle? _showWindowRegistration;
    private FileLoggerProvider? _fileLog;
    private ILogger _logger = NullLogger.Instance;
    private IHost? _host;
    private TrayIcon? _trayIcon;
    private MainWindow? _mainWindow;
    private bool _isShowingError;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // The window runs next to the tray, so each writes its own file.
        var isWindow = HasArgument(e, SettingsArgument);
        _fileLog = new FileLoggerProvider(new FileLoggerOptions
        {
            FilePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HADA",
                "logs",
                isWindow ? "settings-window.log" : "tray.log"),
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
            if (isWindow)
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

        // A sensor that fails must not take the other sensors and the tray icon down with it.
        builder.Services.Configure<HostOptions>(
            options => options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

        builder.Services.AddSingleton<IEventBus, ChannelEventBus>();
        builder.Services.AddSingleton<IEntityRegistry, EntityRegistry>();
        builder.Services.Configure<IpcOptions>(options => options.ClientName = $"tray:{Environment.UserName}");

        // The IPC client starts first so it is subscribed before the sensors publish anything.
        builder.Services.AddSingleton<IpcClient>();
        builder.Services.AddHostedService(services => services.GetRequiredService<IpcClient>());
        builder.Services.AddHostedService<ActiveWindowSensor>();
        builder.Services.AddHostedService<AudioVolumeSensor>();
        builder.Services.AddHostedService<UserActivitySensor>();
        builder.Services.AddHostedService<MediaCaptureSensor>();
        builder.Services.AddHostedService<MicrophoneMuteSensor>();
        _host = builder.Build();

        // Started on the thread pool so hosted services never capture the dispatcher's synchronization context,
        // which would also make the blocking stop in OnExit deadlock.
        await Task.Run(() => _host.StartAsync());

        var ipc = _host.Services.GetRequiredService<IpcClient>();
        _trayIcon = new TrayIcon(() => ipc.IsConnected, () => OpenWindow(page: null), () => Shutdown());
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
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            start.ArgumentList.Add(SettingsArgument);
            if (page is not null)
            {
                start.ArgumentList.Add(PageArgument);
                start.ArgumentList.Add(page);
            }

            Process.Start(start)?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            LogWindowStartFailed(_logger, ex);
            System.Windows.MessageBox.Show(ex.Message, Loc.Get("Error_UnexpectedTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void StartWindow(string? page)
    {
        // One window per user and privilege level: the administrator copy opened with "Unlock editing" may run
        // next to the normal one for a moment, while that one is closing.
        var name = Elevation.IsElevated ? @"Local\HADA.Window.Admin" : @"Local\HADA.Window";
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

            Shutdown();
            return;
        }

        _showWindowSignal = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, name + ".Show");
        _showWindowRegistration = ThreadPool.RegisterWaitForSingleObject(
            _showWindowSignal,
            (_, _) => Dispatcher.InvokeAsync(BringWindowToFront),
            state: null,
            Timeout.Infinite,
            executeOnlyOnce: false);

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
        _mainWindow.Closed += (_, _) => Shutdown();
        _mainWindow.Show();
        _mainWindow.Activate();
    }

    private void BringWindowToFront()
    {
        if (_mainWindow is null)
        {
            return;
        }

        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

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
