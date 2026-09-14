using HADA.Core.Abstractions;
using HADA.Core.Messaging;
using HADA.Service;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "HADA");
builder.Services.AddSingleton<IEventBus, ChannelEventBus>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
