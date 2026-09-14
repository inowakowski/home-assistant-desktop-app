using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using HADA.Core.Abstractions;

namespace HADA.Core.Messaging;

/// <summary>
/// <see cref="IEventBus"/> backed by one bounded <see cref="Channel{T}"/> per subscription,
/// so a slow subscriber never delays delivery to others unless it opts into <see cref="BackpressureMode.Wait"/>.
/// </summary>
public sealed class ChannelEventBus : IEventBus, IAsyncDisposable
{
    private readonly Lock _gate = new();
    private Subscription[] _subscriptions = [];
    private bool _disposed;

    public ValueTask PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default) where TEvent : notnull
    {
        ArgumentNullException.ThrowIfNull(@event);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Copy-on-write array: publishing never takes the lock.
        var subscriptions = Volatile.Read(ref _subscriptions);
        for (var i = 0; i < subscriptions.Length; i++)
        {
            var pending = subscriptions[i].DeliverAsync(@event, cancellationToken);
            if (!pending.IsCompletedSuccessfully)
            {
                return PublishSlowAsync(pending, subscriptions, i + 1, @event, cancellationToken);
            }
        }

        return ValueTask.CompletedTask;
    }

    public IEventSubscription<TEvent> Subscribe<TEvent>(EventSubscriptionOptions? options = null) where TEvent : notnull
    {
        options ??= EventSubscriptionOptions.Default;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.Capacity);

        var subscription = new Subscription<TEvent>(this, options);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _subscriptions = [.. _subscriptions, subscription];
        }

        return subscription;
    }

    public ValueTask DisposeAsync()
    {
        Subscription[] subscriptions;
        lock (_gate)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
            subscriptions = _subscriptions;
            _subscriptions = [];
        }

        foreach (var subscription in subscriptions)
        {
            subscription.Complete();
        }

        return ValueTask.CompletedTask;
    }

    private static async ValueTask PublishSlowAsync(
        ValueTask pending, Subscription[] subscriptions, int next, object @event, CancellationToken cancellationToken)
    {
        await pending.ConfigureAwait(false);
        for (var i = next; i < subscriptions.Length; i++)
        {
            await subscriptions[i].DeliverAsync(@event, cancellationToken).ConfigureAwait(false);
        }
    }

    private void Remove(Subscription subscription)
    {
        lock (_gate)
        {
            if (Array.IndexOf(_subscriptions, subscription) >= 0)
            {
                _subscriptions = Array.FindAll(_subscriptions, s => !ReferenceEquals(s, subscription));
            }
        }
    }

    private abstract class Subscription
    {
        public abstract ValueTask DeliverAsync(object @event, CancellationToken cancellationToken);

        public abstract void Complete();
    }

    private sealed class Subscription<TEvent> : Subscription, IEventSubscription<TEvent>
    {
        private readonly ChannelEventBus _owner;
        private readonly Channel<TEvent> _channel;

        public Subscription(ChannelEventBus owner, EventSubscriptionOptions options)
        {
            _owner = owner;
            _channel = Channel.CreateBounded<TEvent>(new BoundedChannelOptions(options.Capacity)
            {
                FullMode = options.Backpressure switch
                {
                    BackpressureMode.Wait => BoundedChannelFullMode.Wait,
                    BackpressureMode.DropOldest => BoundedChannelFullMode.DropOldest,
                    BackpressureMode.DropNewest => BoundedChannelFullMode.DropWrite,
                    _ => throw new ArgumentOutOfRangeException(nameof(options), options.Backpressure, null),
                },
                SingleReader = false,
                SingleWriter = false,
            });
        }

        public override ValueTask DeliverAsync(object @event, CancellationToken cancellationToken)
        {
            if (@event is not TEvent typed || _channel.Writer.TryWrite(typed))
            {
                return ValueTask.CompletedTask;
            }

            return WaitAndWriteAsync(typed, cancellationToken);
        }

        private async ValueTask WaitAndWriteAsync(TEvent @event, CancellationToken cancellationToken)
        {
            // WaitToWriteAsync returns false once the subscription is disposed; the event is then dropped.
            while (await _channel.Writer.WaitToWriteAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_channel.Writer.TryWrite(@event))
                {
                    return;
                }
            }
        }

        public IAsyncEnumerable<TEvent> ReadAllAsync(CancellationToken cancellationToken = default) =>
            _channel.Reader.ReadAllAsync(cancellationToken);

        public bool TryRead([MaybeNullWhen(false)] out TEvent @event) => _channel.Reader.TryRead(out @event);

        public override void Complete() => _channel.Writer.TryComplete();

        public ValueTask DisposeAsync()
        {
            _owner.Remove(this);
            Complete();
            return ValueTask.CompletedTask;
        }
    }
}
