using System.Net;
using System.Net.Sockets;

namespace HADA.Core;

/// <summary>
/// Tells addresses of the network the computer is on, or of the computer itself, from addresses out on the
/// internet. What is sent unencrypted to the former stays among the user's own devices; to the latter it passes
/// through networks that are nobody's to trust.
/// </summary>
public static class NetworkScope
{
    // Names that are only ever answered on the network one is on.
    private static readonly string[] LocalSuffixes = [".local", ".lan", ".home", ".internal", ".home.arpa", ".localdomain", ".localhost"];

    /// <summary>
    /// Whether the address is this computer's, one of a private network (as home routers and VPNs hand out), or
    /// one that only works on the link it is on.
    /// </summary>
    public static bool IsLocal(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal || address.IsIPv6SiteLocal;
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[4];
        address.TryWriteBytes(bytes, out _);
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168)
            || (bytes[0] == 169 && bytes[1] == 254)

            // Shared address space, which carrier-grade NAT and VPNs such as Tailscale use.
            || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127);
    }

    /// <summary>The names of networks written one per line, each once, without the empty lines.</summary>
    public static IReadOnlyList<string> Names(string? networks) =>
        (networks ?? string.Empty).Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// Whether a host, as typed into an address, reads like one of the network the computer is on: a local
    /// address, a name without a dot, or a name with an ending only used locally. A guess to word a hint by,
    /// as the name is not looked up; what it stands for is what counts when connecting.
    /// </summary>
    public static bool LooksLocal(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var name = host.Trim().TrimEnd('.');
        if (name.Length == 0)
        {
            return false;
        }

        if (IPAddress.TryParse(name.Trim('[', ']'), out var address))
        {
            return IsLocal(address);
        }

        return !name.Contains('.', StringComparison.Ordinal)
            || LocalSuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }
}
