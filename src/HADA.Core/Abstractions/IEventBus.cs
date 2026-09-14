using System.Diagnostics.CodeAnalysis;

namespace HADA.Core.Abstractions;

/// <summary>
/// In-process publish/subscribe bus. Sensors, actions and communication engines talk only through this.
/// </summary>
public interface IEventBus
{
    /// <summary>
    /// Delivers <paramref name="event"/> to every subscription whose event type it is assignable to.
    /// May wait if a subscriber uses <see cref="BackpressureMode.Wait"/> and its buffer is full.
    /// </summary>
    ValueTask PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default) where TEvent : notnull;

    /// <summary>
    /// Registers a subscription immediately; events published after this call are buffered until read.
    /// Subscribing to a base type (or <see cref="object"/>) receives all derived events.
    /// Dispose the subscription to stop receiving.
    /// </summary>
    IEventSubscription<TEvent> Subscribe<TEvent>(EventSubscriptionOptions? options = null) where TEvent : notnull;
}

public interface IEventSubscription<TEvent> : IAsyncDisposable
{
    /// <summary>Reads events until the subscription or bus is disposed, or the token is cancelled.</summary>
    IAsyncEnumerable<TEvent> ReadAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads one buffered event without waiting.</summary>
    bool TryRead([MaybeNullWhen(false)] out TEvent @event);
}

/// <summary>What happens when a subscriber's buffer is full.</summary>
public enum BackpressureMode
{
    /// <summary>The publisher waits for space. No events are lost.</summary>
    Wait,

    /// <summary>The oldest buffered event is discarded. Suited to telemetry where only the latest state matters.</summary>
    DropOldest,

    /// <summary>The incoming event is discarded.</summary>
    DropNewest,
}

public sealed record EventSubscriptionOptions
{
    public static EventSubscriptionOptions Default { get; } = new();

    /// <summary>Maximum number of buffered, unread events.</summary>
    public int Capacity { get; init; } = 1024;

    public BackpressureMode Backpressure { get; init; } = BackpressureMode.Wait;
}
