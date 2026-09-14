using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Messaging;
using HADA.Engine.Mqtt;
using HADA.Engine.WebSocket;
using HADA.Ipc;
using HADA.Platform.Windows.Actions;
using HADA.Platform.Windows.Sensors;
using HADA.Service;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "HADA");

builder.Services.AddSingleton<IEventBus, ChannelEventBus>();
builder.Services.AddSingleton<IEntityRegistry, EntityRegistry>();

// Each engine stays idle until its configuration section is filled in.
builder.Services.Configure<MqttOptions>(builder.Configuration.GetSection(MqttOptions.SectionName));
builder.Services.AddSingleton<ICommunicationEngine, MqttEngine>();
builder.Services.Configure<HaWebSocketOptions>(builder.Configuration.GetSection(HaWebSocketOptions.SectionName));
builder.Services.AddSingleton<ICommunicationEngine, HaWebSocketEngine>();

// Engines start first so they are listening before sensors, actions and tray clients register.
builder.Services.AddHostedService<CommunicationEngineHost>();
builder.Services.AddHostedService<IpcServer>();
builder.Services.AddHostedService<CpuLoadSensor>();
builder.Services.AddHostedService<LockScreenAction>();

var host = builder.Build();
host.Run();
