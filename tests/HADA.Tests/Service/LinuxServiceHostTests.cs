using HADA.Core.Actions;
using HADA.Core.Sensors;
using HADA.Ipc;
using HADA.Service;
using HADA.Service.CustomSensors;
using HADA.Service.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace HADA.Tests.Service;

/// <summary>The service as Linux puts it together, built without starting it; on the CI's Ubuntu.</summary>
public sealed class LinuxServiceHostTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "HADA.Tests." + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [LinuxFact]
    public void Every_part_of_the_service_can_be_created_on_linux()
    {
        using var host = ServiceHost.CreateBuilder([], new SettingsStore(_folder)).Build();

        var parts = host.Services.GetServices<IHostedService>().Select(part => part.GetType()).ToArray();

        Assert.Contains(typeof(IpcServer), parts);
        Assert.Contains(typeof(CpuLoadSensor), parts);
        Assert.Contains(typeof(BatterySensor), parts);
        Assert.Contains(typeof(DiskUsageSensor), parts);
        Assert.Contains(typeof(ActiveUserSensor), parts);
        Assert.Contains(typeof(LockScreenAction), parts);
        Assert.Contains(typeof(PowerActions), parts);
        Assert.Equal(typeof(CustomSensorHost), parts[^1]);
    }
}
