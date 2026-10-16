using System.Diagnostics;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Threading;
using HADA.Core;
using HADA.Core.Abstractions;
using HADA.Core.Actions;
using HADA.Core.Entities;
using HADA.Core.Hosting;
using HADA.Core.Logging;
using HADA.Core.Messaging;
using HADA.Core.Models;
using HADA.Ipc;
using HADA.Platform.Linux;
using HADA.Platform.MacOS;
using HADA.Presentation;
using HADA.Presentation.Localization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HADA.App;

/// <summary>
/// What runs in the user's session on Linux and macOS, as the tray app does on Windows: the service, started and
/// stopped with the app; the sensors of the session, sent to it; and the icon in the system tray, from which the
/// window is opened again and the app is ended.
/// </summary>
internal sealed partial class Agent : IAsyncDisposable
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly FileLoggerProvider _log;
    private readonly ILogger _logger;
    private readonly IHost _host;
    private readonly ServiceProcess? _service;
    private readonly TrayIcon _icon;
    private readonly DispatcherTimer _iconState;
    private readonly IAsyncDisposable? _notifications;
    private readonly IDisposable? _shortcuts;

    private Agent(
        FileLoggerProvider log,
        ILogger logger,
        IHost host,
        ServiceProcess? service,
        TrayIcon icon,
        DispatcherTimer iconState,
        IAsyncDisposable? notifications,
        IDisposable? shortcuts)
    {
        _log = log;
        _logger = logger;
        _host = host;
        _service = service;
        _icon = icon;
        _iconState = iconState;
        _notifications = notifications;
        _shortcuts = shortcuts;
    }

    /// <summary>Whether this system has its agent in the app; Windows has the tray app for it.</summary>
    public static bool IsWanted => !OperatingSystem.IsWindows();

    /// <param name="showWindow">Opens the window, from the icon or its menu.</param>
    /// <param name="exit">Ends the app, from the icon's menu.</param>
    public static async Task<Agent> StartAsync(Application application, Action showWindow, Action exit)
    {
        var log = new FileLoggerProvider(new FileLoggerOptions { FilePath = Path.Combine(AppFolders.UserLogs, "app.log") });
        var logger = log.CreateLogger("HADA.App.Agent");

        // The service is in the folder next to the app's, in a Linux package as in a portable copy; in the app's own
        // folder in a Mac's app, where all code is in Contents/MacOS. Not while developing.
        ServiceProcess? service = null;
        var root = new[] { AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "..") }
            .Select(Path.GetFullPath)
            .FirstOrDefault(folder => Directory.Exists(Path.Combine(folder, "service")));
        if (root is not null)
        {
            service = new ServiceProcess(root, logger);
            try
            {
                service.Start();
            }
            catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                LogServiceNotStarted(logger, ex.Message);
                service.Dispose();
                service = null;
            }
        }

        var host = BuildHost(log);
        await host.StartAsync();

        var ipc = host.Services.GetRequiredService<IpcClient>();
        var (shortcuts, setShortcuts) = CreateShortcuts();
        var (icon, iconState) = CreateIcon(application, ipc, host.Services.GetRequiredService<QuickActions>(), setShortcuts, logger, showWindow, exit);
        var notifications = OperatingSystem.IsLinux() ? await ShowNotificationsAsync(host, logger) : null;
        if (OperatingSystem.IsMacOS())
        {
            ShowMacNotifications(host, logger);
        }

        return new Agent(log, logger, host, service, icon, iconState, notifications, shortcuts);
    }

    public async ValueTask DisposeAsync()
    {
        if (_notifications is not null)
        {
            await _notifications.DisposeAsync();
        }

        _iconState.Stop();
        _icon.IsVisible = false;
        _icon.Dispose();
        _shortcuts?.Dispose();

        // The session's sensors say goodbye first, while the service still hears them.
        using (var stopping = new CancellationTokenSource(StopTimeout))
        {
            await _host.StopAsync(stopping.Token);
        }

        _host.Dispose();
        _service?.Dispose();
        _log.Dispose();
    }

    private static IHost BuildHost(FileLoggerProvider log)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Logging.AddProvider(log);

        // A sensor that fails must not take the others and the icon down with it.
        builder.Services.Configure<HostOptions>(options => options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);
        builder.Services.AddSingleton<IEventBus, ChannelEventBus>();
        builder.Services.AddSingleton<IEntityRegistry, EntityRegistry>();
        builder.Services.Configure<IpcOptions>(options => options.ClientName = $"tray:{Environment.UserName}");

        // The IPC client starts first so it is subscribed before the sensors publish anything.
        builder.Services.AddSingleton<IpcClient>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<IpcClient>());

        // Programs the service's buttons start, and the quick actions of the icon's menu.
        builder.Services.AddHostedService<LaunchAction>();
        builder.Services.AddSingleton<QuickActions>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<QuickActions>());
        if (OperatingSystem.IsLinux())
        {
            builder.Services.AddLinuxSessionEntities(() => TimeSpan.FromSeconds(UserPreferences.IdleSeconds));
        }
        else if (OperatingSystem.IsMacOS())
        {
            builder.Services.AddMacSessionEntities(() => TimeSpan.FromSeconds(UserPreferences.IdleSeconds));
        }

        return builder.Build();
    }

    /// <summary>
    /// Has the desktop show what Home Assistant sends, and reports a pressed button back as an event, to the Home
    /// Assistant the notification came from.
    /// </summary>
    [SupportedOSPlatform("linux")]
    private static async Task<IAsyncDisposable?> ShowNotificationsAsync(IHost host, ILogger logger)
    {
        var bus = host.Services.GetRequiredService<IEventBus>();
        var notifications = new LinuxNotifications(
            (action, origin) => _ = bus.PublishAsync(new DeviceEvent { Name = DeviceEvent.NotificationAction, Value = action, Target = origin }).AsTask(),
            address => OpenInBrowser(address, logger),
            logger);
        if (!await notifications.StartAsync())
        {
            return null;
        }

        host.Services.GetRequiredService<NotificationPresenter>().Show = request => _ = notifications.ShowAsync(request);
        return notifications;
    }

    /// <summary>
    /// The icon in the system tray. Its menu has the quick actions, the window, a Home Assistant dashboard for each
    /// there is, which opens in the browser, and the way to end the app.
    /// </summary>
    /// <summary>Has Notification Center show what Home Assistant sends: a title and a text, without its picture or buttons.</summary>
    [SupportedOSPlatform("macos")]
    private static void ShowMacNotifications(IHost host, ILogger logger) =>
        host.Services.GetRequiredService<NotificationPresenter>().Show = request => _ = Task.Run(() =>
        {
            if (MacNotifications.Show(request) is { } reason)
            {
                LogNotificationNotShown(logger, reason);
            }
        });

    /// <summary>
    /// What listens for the quick actions' shortcuts: Carbon's hot keys on a Mac, X11's key grabs on Linux.
    /// Nothing on Wayland, which lets no app listen for keys meant for others.
    /// </summary>
    private static (IDisposable? Owner, Func<IEnumerable<(string Shortcut, Action Pressed)>, IReadOnlyList<string>>? Set) CreateShortcuts()
    {
        if (OperatingSystem.IsMacOS())
        {
            var mac = new MacHotkeys();
            return (mac, mac.Set);
        }

        if (OperatingSystem.IsLinux() && X11Hotkeys.Open() is { } x11)
        {
            return (x11, x11.Set);
        }

        return (null, null);
    }

    private static (TrayIcon Icon, DispatcherTimer State) CreateIcon(
        Application application,
        IpcClient ipc,
        QuickActions quickActions,
        Func<IEnumerable<(string Shortcut, Action Pressed)>, IReadOnlyList<string>>? setShortcuts,
        ILogger logger,
        Action showWindow,
        Action exit)
    {
        var menu = new NativeMenu();
        string? shown = null;
        void Update()
        {
            // Built again only when something in it changed: the service sends the quick actions, and the window
            // keeps the dashboards.
            var actions = quickActions.Current;
            var dashboards = UserPreferences.AllDashboards;
            var content = string.Join(
                '\n',
                actions.Select(action => $"{action.Id}\t{action.Name}\t{action.Hotkey}")
                    .Concat(dashboards.Select(dashboard => $"{dashboard.Id}\t{dashboard.Name}\t{dashboard.Address}")));
            if (content == shown)
            {
                return;
            }

            shown = content;

            // The shortcuts, where the system lets them be listened for; what cannot be had is said in the log.
            var refused = setShortcuts?.Invoke(
                actions.Where(action => action.Hotkey.Length > 0).Select(action => (action.Hotkey, (Action)(() => quickActions.Choose(action))))) ?? [];
            foreach (var shortcut in refused)
            {
                LogShortcutNotSet(logger, shortcut);
            }

            menu.Items.Clear();
            foreach (var action in actions)
            {
                var item = new NativeMenuItem(action.Name);
                if (setShortcuts is not null && action.Hotkey.Length > 0 && !refused.Contains(action.Hotkey) && GestureOf(action.Hotkey) is { } gesture)
                {
                    // Shown at the right edge, as menus show shortcuts; listened for above.
                    item.Gesture = gesture;
                }

                item.Click += (_, _) => quickActions.Choose(action);
                menu.Items.Add(item);
            }

            if (actions.Count > 0)
            {
                menu.Items.Add(new NativeMenuItemSeparator());
            }

            var open = new NativeMenuItem(Loc.Get("Tray_Open"));
            open.Click += (_, _) => showWindow();
            menu.Items.Add(open);

            // One dashboard needs no name; several are told apart by the Home Assistant each shows.
            foreach (var dashboard in dashboards)
            {
                var item = new NativeMenuItem(dashboards.Count == 1 ? Loc.Get("Tray_Dashboard") : Loc.Format("Tray_DashboardNamed", dashboard.Name));
                item.Click += (_, _) => OpenInBrowser(dashboard.Address.AbsoluteUri, logger);
                menu.Items.Add(item);
            }

            var quit = new NativeMenuItem(Loc.Get("Tray_Exit"));
            quit.Click += (_, _) => exit();
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(quit);
        }

        Update();
        quickActions.Changed += _ => Dispatcher.UIThread.Post(Update);

        using var image = AssetLoader.Open(new Uri("avares://HADA.App/Assets/hada.ico"));
        var icon = new TrayIcon
        {
            Icon = new WindowIcon(image),
            ToolTipText = Loc.Get("Tray_Waiting"),
            Menu = menu,
        };
        icon.Clicked += (_, _) => showWindow();
        TrayIcon.SetIcons(application, [icon]);

        // The tooltip says whether the service hears this session; the menu follows the settings.
        var state = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) =>
        {
            icon.ToolTipText = Loc.Get(ipc.IsConnected ? "Tray_Connected" : "Tray_Waiting");
            Update();
        });
        state.Start();
        return (icon, state);
    }

    /// <summary>A shortcut as the menu shows it; Win is the Command key on a Mac and Super on Linux, which Avalonia calls Meta.</summary>
    private static KeyGesture? GestureOf(string shortcut)
    {
        try
        {
            return KeyGesture.Parse(shortcut.Replace("Win+", "Meta+", StringComparison.OrdinalIgnoreCase).Replace("Windows+", "Meta+", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Opens an <c>http</c> or <c>https</c> address in the user's browser.</summary>
    private static void OpenInBrowser(string address, ILogger logger)
    {
        try
        {
            Process.Start(new ProcessStartInfo(address) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            LogNotOpened(logger, address, ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The service could not be started: {Reason}")]
    private static partial void LogServiceNotStarted(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The shortcut {Shortcut} of a quick action could not be set; the menu entry still works.")]
    private static partial void LogShortcutNotSet(ILogger logger, string shortcut);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A notification could not be shown: {Reason}")]
    private static partial void LogNotificationNotShown(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "'{Address}' could not be opened in the browser: {Reason}")]
    private static partial void LogNotOpened(ILogger logger, string address, string reason);
}
