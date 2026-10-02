using HADA.Ipc;
using HADA.Platform.Windows.Actions;
using HADA.Platform.Windows.Sensors;
using HADA.Service;
using HADA.Service.CustomSensors;
using HADA.Service.Settings;
using HADA.Service.Updates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace HADA.Tests.Service;

/// <summary>
/// Builds the service as <c>Program</c> does, without starting it: a constructor parameter nothing can supply
/// would otherwise only show when the real service starts.
/// </summary>
public sealed class ServiceHostTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "HADA.Tests." + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // The log file may still be held for a moment; a leftover folder in the temp directory is harmless.
        }
    }

    [Fact]
    public void Every_part_of_the_service_can_be_created()
    {
        using var host = ServiceHost.CreateBuilder([], new SettingsStore(_folder)).Build();

        var parts = host.Services.GetServices<IHostedService>().Select(part => part.GetType()).ToArray();

        Assert.Contains(typeof(IpcServer), parts);
        Assert.Contains(typeof(PowerActions), parts);
        Assert.Contains(typeof(NetworkSensor), parts);
        Assert.Contains(typeof(DiskUsageSensor), parts);
        Assert.Contains(typeof(ActiveUserSensor), parts);
        Assert.Contains(typeof(UpdateChecker), parts);

        // Custom sensors come last, so they can never take the id of a built-in entity first.
        Assert.Equal(typeof(CustomSensorHost), parts[^1]);
        Assert.NotNull(host.Services.GetRequiredService<IServiceControl>());
    }

    [Fact]
    public void A_test_run_is_not_mistaken_for_a_second_copy_of_the_service()
    {
        // True only for a console copy of the service next to another one, which a test host never is, unless a
        // HADA service runs on this very computer; then the answer is rightly yes.
        Assert.Equal(
            System.Diagnostics.Process.GetProcessesByName("HADA.Service").Length > 0,
            ServiceHost.IsUnwantedSecondCopy());
    }
}
