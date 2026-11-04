using System.Net;
using HADA.Core;

namespace HADA.Engine.Mqtt;

/// <summary>
/// The part that keeps the password off networks it could be read on, as the direct connection does with the
/// access token. With TLS the connection is encrypted and the broker's certificate is checked. Without it the
/// password, the readings, the commands and the notifications travel as plain text. On the network the computer is
/// on that is how most brokers are reached; to an address out on the internet it is refused, unless the user
/// allowed it for that server.
/// </summary>
public sealed partial class MqttEngine
{
    /// <summary>The broker is at a public address, TLS is off, and that was not allowed.</summary>
    public const string IssueUnencrypted = "mqtt_unencrypted_public";

    // Why no connection is made, when that is something to put right in the settings.
    private volatile string? _issue;

    public string? Issue => _issue;

    /// <summary>
    /// Throws <see cref="MqttUnencryptedException"/> when connecting to <paramref name="address"/> would send the
    /// password and everything else unencrypted to an address that is not of this computer or of a private network.
    /// </summary>
    /// <param name="address">The very address connected to, as <see cref="BrokerLocator"/> found it.</param>
    public static void EnsureEncryptedOrLocal(MqttOptions options, IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(address);
        if (options.UseTls || options.AllowUnencrypted || NetworkScope.IsLocal(address))
        {
            return;
        }

        throw new MqttUnencryptedException(options.Host ?? address.ToString(), address);
    }
}

/// <summary>The broker would be reached without encryption at a public address, which was not allowed.</summary>
public sealed class MqttUnencryptedException(string host, IPAddress address)
    : Exception(
        $"Not connecting to MQTT broker '{host}' at {address}: TLS is off and the address is a public one, so the password and everything "
        + "sent would travel in the open. Turn TLS on, or allow the unencrypted connection for this server on the Connections page.");
