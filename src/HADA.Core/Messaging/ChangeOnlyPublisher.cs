using System.Collections.Frozen;
using System.Text.Json;
using HADA.Core.Abstractions;
using HADA.Core.Models;

namespace HADA.Core.Messaging;

/// <summary>
/// Publishes a sensor's reading only when its state or attributes differ from the last one published,
/// so polling sensors do not flood Home Assistant with identical values. Not thread-safe: use one per sensor loop.
/// </summary>
public sealed class ChangeOnlyPublisher(IEventBus bus, string? source = null)
{
    private readonly Dictionary<string, string> _last = new(StringComparer.Ordinal);

    public async ValueTask PublishAsync(
        string sensorId,
        string state,
        IReadOnlyDictionary<string, object?>? attributes = null,
        CancellationToken cancellationToken = default)
    {
        var fingerprint = attributes is null ? state : state + "\n" + JsonSerializer.Serialize(attributes);
        if (_last.TryGetValue(sensorId, out var previous) && previous == fingerprint)
        {
            return;
        }

        _last[sensorId] = fingerprint;
        await bus.PublishAsync(
                new TelemetryEvent
                {
                    SensorId = sensorId,
                    State = state,
                    Attributes = attributes ?? FrozenDictionary<string, object?>.Empty,
                    Source = source,
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Makes the next reading for <paramref name="sensorId"/> go out even if it is unchanged.</summary>
    public void Forget(string sensorId) => _last.Remove(sensorId);
}
