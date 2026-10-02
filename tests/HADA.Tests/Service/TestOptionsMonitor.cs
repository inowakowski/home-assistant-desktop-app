using Microsoft.Extensions.Options;

namespace HADA.Tests.Service;

/// <summary>An options monitor whose value the test changes by hand, notifying listeners like a configuration reload.</summary>
internal sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    private readonly List<Action<T, string?>> _listeners = [];

    public T CurrentValue { get; private set; } = value;

    public T Get(string? name) => CurrentValue;

    public IDisposable OnChange(Action<T, string?> listener)
    {
        lock (_listeners)
        {
            _listeners.Add(listener);
        }

        return new Registration(() =>
        {
            lock (_listeners)
            {
                _listeners.Remove(listener);
            }
        });
    }

    public void Set(T newValue)
    {
        CurrentValue = newValue;
        Action<T, string?>[] listeners;
        lock (_listeners)
        {
            listeners = [.. _listeners];
        }

        foreach (var listener in listeners)
        {
            listener(newValue, Options.DefaultName);
        }
    }

    private sealed class Registration(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
