using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Messaging;
using HADA.Engine.Mqtt;
using HADA.Engine.WebSocket;
using HADA.Ipc;
using HADA.Platform.Windows.Actions;
using HADA.Platform.Windows.Sensors;
using HADA.Service;
using HADA.Service.Logging;
using HADA.Service.Settings;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "HADA");

// Settings saved from the tray's settings window, added last so they override appsettings.json.
var settingsStore = new SettingsStore();
var storedSettings = new StoredSettingsConfigurationSource(settingsStore);
((IConfigurationBuilder)builder.Configuration).Add(storedSettings);
builder.Services.AddSingleton(settingsStore);
builder.Services.AddSingleton(storedSettings.Provider);

// Recent log entries, shown in the settings window.
var logBuffer = new LogBuffer();
builder.Logging.AddProvider(new InMemoryLoggerProvider(logBuffer));
builder.Services.AddSingleton(logBuffer);

builder.Services.AddSingleton<IEventBus, ChannelEventBus>();
builder.Services.AddSingleton<IEntityRegistry, EntityRegistry>();

builder.Services.Configure<MqttOptions>(builder.Configuration.GetSection(MqttOptions.SectionName));
builder.Services.Configure<HaWebSocketOptions>(builder.Configuration.GetSection(HaWebSocketOptions.SectionName));
builder.Services.Configure<EntityOptions>(builder.Configuration.GetSection(EntityOptions.SectionName));

builder.Services.AddSingleton<TelemetryCache>();
builder.Services.AddSingleton<EngineSupervisor>();
builder.Services.AddSingleton<IServiceControl, ServiceControl>();

// Hosted services start in this order: everything that listens starts before the sensors and clients that feed it.
builder.Services.AddHostedService(services => services.GetRequiredService<TelemetryCache>());
builder.Services.AddHostedService(services => services.GetRequiredService<EngineSupervisor>());
builder.Services.AddHostedService<IpcServer>();
builder.Services.AddHostedService<CpuLoadSensor>();
builder.Services.AddHostedService<LockScreenAction>();

var host = builder.Build();
host.Run();
