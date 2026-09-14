namespace HADA.Core.Abstractions;

/// <summary>
/// A transport to Home Assistant (e.g. MQTT with discovery, or the WebSocket API).
/// </summary>
/// <remarks>
/// Engines are the only components that know about a transport. Once started, an engine subscribes to
/// <see cref="Models.TelemetryEvent"/> on the <see cref="IEventBus"/> and forwards readings to Home Assistant,
/// and publishes commands received from Home Assistant as <see cref="Models.ActionCommand"/>.
/// </remarks>
public interface ICommunicationEngine : IAsyncDisposable
{
    /// <summary>Short engine name, used as <see cref="Models.ActionCommand.Origin"/>.</summary>
    string Name { get; }

    EngineConnectionState State { get; }

    /// <summary>Connects to Home Assistant and starts bridging the event bus.</summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>Stops bridging and disconnects gracefully.</summary>
    Task StopAsync(CancellationToken cancellationToken);
}

public enum EngineConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Faulted,
}
