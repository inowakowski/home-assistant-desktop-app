using System.Net;
using System.Net.Sockets;
using HADA.Core;
using Microsoft.Extensions.Logging;

namespace HADA.Engine.WebSocket;

/// <summary>
/// The part that keeps the access token off networks it could be read on. An <c>https</c> address is encrypted,
/// and Windows checks the server's certificate. An <c>http</c> address is not: the token, the commands and the
/// notifications travel as plain text. On the network the computer is on that is how most Home Assistants are
/// reached; to an address out on the internet it is refused, unless the user allowed it for that server.
/// </summary>
public sealed partial class HaWebSocketEngine
{
    /// <summary>The address is an unencrypted one on the internet, and that was not allowed.</summary>
    public const string IssueUnencrypted = "unencrypted_public";

    // What keeps the engine from connecting at all, as opposed to _issue, which is about a connection that is there.
    private volatile string? _connectionIssue;

    /// <summary>
    /// Throws <see cref="HaUnencryptedException"/> when connecting would send the access token unencrypted to an
    /// address that is not of this computer or of a private network.
    /// </summary>
    private static async Task EnsureEncryptedOrLocalAsync(Uri baseUrl, bool allowUnencrypted, CancellationToken cancellationToken)
    {
        if (allowUnencrypted || baseUrl.Scheme != Uri.UriSchemeHttp)
        {
            return;
        }

        IPAddress[] addresses;
        if (IPAddress.TryParse(baseUrl.DnsSafeHost, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await Dns.GetHostAddressesAsync(baseUrl.DnsSafeHost, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or ArgumentException)
            {
                // A name that cannot be found: connecting fails for that reason, and says so.
                return;
            }
        }

        // One public address among them is enough: which one the connection is made to is not this code's choice.
        if (addresses.Any(address => !NetworkScope.IsLocal(address)))
        {
            throw new HaUnencryptedException(baseUrl.Host);
        }
    }

    /// <summary>Remembers why no connection is made, and says so in the log when that changes, not at every attempt.</summary>
    private void SetConnectionIssue(string? issue)
    {
        if (_connectionIssue == issue)
        {
            return;
        }

        _connectionIssue = issue;
        if (issue == IssueUnencrypted)
        {
            LogUnencryptedRefused(_logger, _baseUrl!.Host);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Not connecting to Home Assistant at '{Host}': the address is not encrypted (http) and is a public one, so the access token would travel in the open. Use an https:// address, or allow the unencrypted connection for this server on the Connections page.")]
    private static partial void LogUnencryptedRefused(ILogger logger, string host);
}

internal sealed class HaUnencryptedException(string host)
    : Exception($"The connection to '{host}' would not be encrypted, and the address is a public one: the access token would travel in the open.");
