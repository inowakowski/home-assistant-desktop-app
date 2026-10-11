namespace HADA.Core.Abstractions;

/// <summary>
/// Which networks the computer is connected to right now, by the names the system knows them by: a Wi-Fi
/// network's own name, and whatever the system calls a wired one or a VPN.
/// </summary>
public interface INetworkLocation
{
    /// <summary>The names of the connected networks; empty when there are none, or the system does not tell.</summary>
    IReadOnlyList<string> ConnectedNetworks { get; }

    /// <summary>Raised when the computer's connections may have changed. What they are now is to be asked for.</summary>
    event EventHandler? Changed;
}

/// <summary>For where the networks cannot be told: there are none to name, and nothing ever changes.</summary>
public sealed class UnknownNetworkLocation : INetworkLocation
{
    public static UnknownNetworkLocation Instance { get; } = new();

    public IReadOnlyList<string> ConnectedNetworks => [];

    public event EventHandler? Changed
    {
        add { }
        remove { }
    }
}
