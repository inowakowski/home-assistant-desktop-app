using HADA.Core.Abstractions;

namespace HADA.Service;

/// <summary>Starts every registered communication engine with the host and stops them on shutdown.</summary>
public sealed class CommunicationEngineHost(IEnumerable<ICommunicationEngine> engines) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        Task.WhenAll(engines.Select(engine => engine.StartAsync(cancellationToken)));

    public Task StopAsync(CancellationToken cancellationToken) =>
        Task.WhenAll(engines.Select(engine => engine.StopAsync(cancellationToken)));
}
