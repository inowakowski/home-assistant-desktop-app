using System.Diagnostics;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
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

    private Agent(
        FileLoggerProvider log, ILogger logger, IHost host, ServiceProcess? service, TrayIcon icon, DispatcherTimer iconState, IAsyncDisposable? notifications)
    {
        _log = log;
        _logger = logger;
        _host = host;
        _service = service;
        _icon = icon;
        _iconState = iconState;
        _notifications = notifications;
    }

    /// <summary>Whether this system has its agent in the app; Windows has the tray app for it.</summary>
    public static bool IsWanted => !OperatingSystem.IsWindows();

    /// <param name="showWindow">Opens the window, from the icon or its menu.</param>
    /// <param name="exit">Ends the app, from the icon's menu.</param>
    public static async Task<Agent> StartAsync(Application application, Action showWindow, Action exit)
    {
        var log = new FileLoggerProvider(new FileLoggerOptions { FilePath = Path.Combine(AppFolders.UserLogs, "app.log") });
        var logger = log.CreateLogger("HADA.App.Agent");

        // The service is in the folder next to the app's, in a package as in a portable copy; not while developing.
        ServiceProcess? service = null;
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
        if (Directory.Exists(Path.Combine(root, "service")))
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
        var (icon, iconState) = CreateIcon(application, ipc, showWindow, exit);
        var notifications = OperatingSystem.IsLinux() ? await ShowNotificationsAsync(host, logger) : null;
        return new Agent(log, logger, host, service, icon, iconState, notifications);
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
        if (OperatingSystem.IsLinux())
        {
            builder.Services.AddLinuxSessionEntities(() => TimeSpan.FromSeconds(UserPreferences.IdleSeconds));
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
            address => Process.Start(new ProcessStartInfo(address) { UseShellExecute = true })?.Dispose(),
            logger);
        if (!await notifications.StartAsync())
        {
            return null;
        }

        host.Services.GetRequiredService<NotificationPresenter>().Show = request => _ = notifications.ShowAsync(request);
        return notifications;
    }

    private static (TrayIcon Icon, DispatcherTimer State) CreateIcon(Application application, IpcClient ipc, Action showWindow, Action exit)
    {
        var open = new NativeMenuItem(Loc.Get("Tray_Open"));
        open.Click += (_, _) => showWindow();
        var quit = new NativeMenuItem(Loc.Get("Tray_Exit"));
        quit.Click += (_, _) => exit();

        using var image = AssetLoader.Open(new Uri("avares://HADA.App/Assets/hada.ico"));
        var icon = new TrayIcon
        {
            Icon = new WindowIcon(image),
            ToolTipText = Loc.Get("Tray_Waiting"),
            Menu = new NativeMenu { Items = { open, new NativeMenuItemSeparator(), quit } },
        };
        icon.Clicked += (_, _) => showWindow();
        TrayIcon.SetIcons(application, [icon]);

        // The tooltip says whether the service hears this session.
        var state = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) =>
            icon.ToolTipText = Loc.Get(ipc.IsConnected ? "Tray_Connected" : "Tray_Waiting"));
        state.Start();
        return (icon, state);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The service could not be started: {Reason}")]
    private static partial void LogServiceNotStarted(ILogger logger, string reason);
}
