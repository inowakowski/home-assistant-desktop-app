namespace HADA.Ipc;

/// <summary>
/// What a user typed into the broker host field. People paste addresses in many shapes, so
/// <c>mqtt://broker.local:1883</c>, <c>mqtts://broker.local</c> and <c>10.0.0.5:8883</c> are all understood.
/// </summary>
/// <param name="Host">The bare host name or IP address.</param>
/// <param name="Port">The port, when the address named one.</param>
/// <param name="UseTls">Whether to use TLS, when the address had a scheme that says so.</param>
public readonly record struct MqttAddress(string Host, int? Port, bool? UseTls)
{
    public static MqttAddress Parse(string input)
    {
        var rest = input.Trim();
        bool? useTls = null;

        var schemeEnd = rest.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0)
        {
            useTls = rest[..schemeEnd].ToLowerInvariant() switch
            {
                "mqtts" or "ssl" or "tls" => true,
                "mqtt" or "tcp" => false,
                _ => null,
            };
            rest = rest[(schemeEnd + "://".Length)..];
        }

        var pathStart = rest.IndexOf('/');
        if (pathStart >= 0)
        {
            rest = rest[..pathStart];
        }

        int? port = null;
        var portSeparator = rest.LastIndexOf(':');

        // More than one colon without brackets is a bare IPv6 address, not a host and a port.
        var hasPort = portSeparator > 0
            && (rest.IndexOf(':') == portSeparator || rest[portSeparator - 1] == ']');
        if (hasPort && int.TryParse(rest[(portSeparator + 1)..], out var parsed))
        {
            port = parsed;
            rest = rest[..portSeparator];
        }

        return new MqttAddress(rest.Trim('[', ']'), port, useTls);
    }

    /// <summary>Returns <paramref name="settings"/> with the host split into host, port and TLS where the host field named them.</summary>
    public static MqttSettings Apply(MqttSettings settings)
    {
        var address = Parse(settings.Host);
        return settings with
        {
            Host = address.Host,
            Port = address.Port ?? settings.Port,
            UseTls = address.UseTls ?? settings.UseTls,
        };
    }
}
