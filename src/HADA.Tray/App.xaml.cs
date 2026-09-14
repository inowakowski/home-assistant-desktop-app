using System.Windows;
using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Messaging;
using HADA.Ipc;
using HADA.Platform.Windows.Sensors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace HADA.Tray;

/// <summary>
/// Runs in the user's session without a main window: hosts the session sensors (active window, volume)
/// and streams them to the service over the IPC pipe. The tray icon is the only UI.
/// </summary>
public partial class App : Application
{
    private Mutex? _singleInstance;
    private IHost? _host;
    private TrayIcon? _trayIcon;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // One tray per user session; a second copy would only duplicate the same readings.
        _singleInstance = new Mutex(initiallyOwned: true, @"Local\HADA.Tray", out var isFirstInstance);
        if (!isFirstInstance)
        {
            Shutdown();
            return;
        }

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
        _trayIcon = new TrayIcon(() => ipc.IsConnected, () => Shutdown());
        _host.Services.GetRequiredService<IHostApplicationLifetime>()
            .ApplicationStopping.Register(() => Dispatcher.InvokeAsync(() => Shutdown()));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        if (_host is not null)
        {
            _host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            _host.Dispose();
        }

        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
