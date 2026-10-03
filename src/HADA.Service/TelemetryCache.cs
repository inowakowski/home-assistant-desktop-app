using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using HADA.Core.Abstractions;
using HADA.Core.Hosting;
using HADA.Core.Models;

namespace HADA.Service;

/// <summary>
/// Remembers the latest reading of every sensor, for the status page and for engines that start later:
/// many sensors report only changes, so a new engine would otherwise not learn their value until the next one.
/// </summary>
public sealed class TelemetryCache(IEventBus bus) : EagerBackgroundService
{
    private readonly ConcurrentDictionary<string, TelemetryEvent> _latest = new(StringComparer.Ordinal);

    public IReadOnlyCollection<TelemetryEvent> Latest => [.. _latest.Values];

    public bool TryGet(string sensorId, [MaybeNullWhen(false)] out TelemetryEvent reading) =>
        _latest.TryGetValue(sensorId, out reading);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var readings = bus.Subscribe<TelemetryEvent>(
            new EventSubscriptionOptions { Capacity = 256, Backpressure = BackpressureMode.DropOldest });
        try
        {
            await foreach (var reading in readings.ReadAllAsync(stoppingToken))
            {
                _latest[reading.SensorId] = reading;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
