using System.Net;
using System.Net.Sockets;

namespace HADA.Engine.Mqtt;

/// <param name="Address">Where to connect.</param>
/// <param name="Candidates">Every address the name stands for, in the order they were tried; just the one for an IP address.</param>
/// <param name="IsRemembered">The name could not be looked up this time, and this is the address it had before.</param>
public sealed record BrokerLocation(IPAddress Address, IReadOnlyList<IPAddress> Candidates, bool IsRemembered = false);

/// <summary>The broker's name could not be turned into an address that accepts connections. The message says why.</summary>
public sealed class BrokerNotFoundException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>
/// Finds the address to reach a broker at. A name can stand for several addresses, as names ending in
/// <c>.local</c> usually do: an IPv4 one and one or more IPv6 ones, and a broker often answers on only some of
/// them. Left to itself, a connection tries them in the order Windows lists them and waits out each one that does
/// not answer, which can take longer than anyone waits. This tries IPv4 first, gives each address a few seconds,
/// and remembers the one that worked, which also carries the connection over a moment in which the name cannot be
/// looked up.
/// </summary>
/// <param name="resolve">Looks a name up; <see cref="Dns.GetHostAddressesAsync(string, CancellationToken)"/> unless a test says otherwise.</param>
/// <param name="connect">Opens and closes a TCP connection, to see whether an address accepts one.</param>
public sealed class BrokerLocator(
    Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null,
    Func<IPEndPoint, CancellationToken, Task>? connect = null)
{
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve = resolve ?? Dns.GetHostAddressesAsync;
    private readonly Func<IPEndPoint, CancellationToken, Task> _connect = connect ?? ConnectAsync;
    private IPAddress? _lastGood;

    public TimeSpan ResolveTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long one address gets to accept a connection before the next one is tried.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Notes the address a connection was made to, to try it first next time.</summary>
    public void Remember(IPAddress address) => _lastGood = address;

    /// <exception cref="BrokerNotFoundException">The name stands for no address, or none of them accepts a connection.</exception>
    public async Task<BrokerLocation> LocateAsync(string host, int port, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            return new BrokerLocation(literal, [literal]);
        }

        IPAddress[] resolved;
        try
        {
            resolved = await ResolveAsync(host, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException or TimeoutException)
        {
            // A name that was found before does not stop existing because one lookup fails: mDNS in particular
            // goes quiet for moments, e.g. right after the computer wakes up.
            return _lastGood is { } remembered
                ? new BrokerLocation(remembered, [remembered], IsRemembered: true)
                : throw new BrokerNotFoundException(DescribeLookupFailure(host, ex), ex);
        }

        var candidates = Order(resolved, _lastGood);
        if (candidates.Count == 0)
        {
            throw new BrokerNotFoundException(DescribeLookupFailure(host, reason: null));
        }

        // One address: nothing to choose from, so the connection itself finds out whether it answers.
        if (candidates.Count == 1)
        {
            return new BrokerLocation(candidates[0], candidates);
        }

        var failures = new List<string>(candidates.Count);
        foreach (var candidate in candidates)
        {
            if (await TryConnectAsync(new IPEndPoint(candidate, port), failures, cancellationToken).ConfigureAwait(false))
            {
                _lastGood = candidate;
                return new BrokerLocation(candidate, candidates);
            }
        }

        throw new BrokerNotFoundException(
            $"The name '{host}' stands for {candidates.Count} addresses, and none accepts a connection on port {port}: "
            + string.Join("; ", failures) + ".");
    }

    /// <summary>IPv4 before IPv6, since that is what brokers at home listen on; the address that worked last time before both.</summary>
    public static IReadOnlyList<IPAddress> Order(IEnumerable<IPAddress> addresses, IPAddress? preferred) =>
    [
        .. addresses
            .Where(address => address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
            .Distinct()
            .OrderBy(address => address.Equals(preferred) ? 0 : address.AddressFamily == AddressFamily.InterNetwork ? 1 : 2),
    ];

    private async Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ResolveTimeout);
        try
        {
            return await _resolve(host, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"no answer within {ResolveTimeout.TotalSeconds:0} s");
        }
    }

    private async Task<bool> TryConnectAsync(IPEndPoint endPoint, List<string> failures, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout);
        try
        {
            await _connect(endPoint, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            failures.Add($"{endPoint.Address} did not answer within {ConnectTimeout.TotalSeconds:0} s");
        }
        catch (SocketException ex)
        {
            failures.Add(ex.SocketErrorCode == SocketError.ConnectionRefused
                ? $"{endPoint.Address} refused the connection"
                : $"{endPoint.Address}: {ex.Message}");
        }

        return false;
    }

    private static string DescribeLookupFailure(string host, Exception? reason)
    {
        var why = reason switch
        {
            null => "it has no address",
            SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData } => "no such name is known",
            _ => reason.Message,
        };

        // What trips people up: such a name is asked for on the local network only.
        var hint = host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            ? " Names ending in .local are found by asking the devices on the same network (mDNS), which does not"
                + " reach other networks, nor through most VPNs. Use the IP address there."
            : string.Empty;
        return $"The name '{host}' could not be looked up: {why}.{hint}";
    }

    private static async Task ConnectAsync(IPEndPoint endPoint, CancellationToken cancellationToken)
    {
        using var socket = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(endPoint, cancellationToken).ConfigureAwait(false);
    }
}
