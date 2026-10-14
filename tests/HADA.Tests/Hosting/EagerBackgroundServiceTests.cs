using HADA.Core.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace HADA.Tests.Hosting;

public sealed class EagerBackgroundServiceTests
{
    [Fact]
    public async Task The_part_before_the_first_wait_is_done_when_starting_returns()
    {
        using var service = new Registers();
        await service.StartAsync(CancellationToken.None);

        Assert.True(service.Registered);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Asked_to_stop_while_still_starting_the_host_stops_without_an_error()
    {
        // As when a portable copy's tray app says "stop" a moment after starting its service.
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddHostedService<StopsTheHost>();
        builder.Services.AddHostedService<Registers>();
        using var host = builder.Build();

        await host.RunAsync().WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task A_service_that_fails_at_once_still_fails_the_start()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddHostedService<FailsAtOnce>();
        using var host = builder.Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
    }

    private sealed class StopsTheHost(IHostApplicationLifetime lifetime) : EagerBackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            lifetime.StopApplication();
            return Task.CompletedTask;
        }
    }

    /// <summary>Like a sensor: registers, with the stopping token, before its first real wait.</summary>
    private sealed class Registers : EagerBackgroundService
    {
        public bool Registered { get; private set; }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Delay(TimeSpan.Zero, stoppingToken);
            Registered = true;
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
    }

    private sealed class FailsAtOnce : EagerBackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
            Task.FromException(new InvalidOperationException("broken"));
    }
}
