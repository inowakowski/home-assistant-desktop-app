using System.Diagnostics;
using HADA.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace HADA.Engine.WebSocket;

/// <summary>
/// The part that chooses between the two addresses a Home Assistant may have: the main one, which works from
/// anywhere, and a local one for the networks the user named as home, where it saves the way out to the
/// internet and back.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The local address is tried only on a home network. Anywhere else it would be an address of somebody
/// else's network, and whatever answers there would be handed the access token.</item>
/// <item>A local address that does not answer is followed by the main one, and tried again the time after.</item>
/// <item>When the computer's networks change, a connection made through the address that is no longer the
/// right one is dropped, and made anew through the other.</item>
/// </list>
/// </remarks>
public sealed partial class HaWebSocketEngine
{
    private readonly INetworkLocation _networks;

    private Uri? _mainUrl;
    private Uri? _localUrl;
    private HashSet<string> _homeNetworks = [];
    private bool _localFailed;
    private long _lastSwitch;

    private void StartFollowingNetworks()
    {
        _localUrl = TryParseBaseUrl(_options.LocalUrl, out var local) ? local : null;
        _homeNetworks = new HashSet<string>(HaWebSocketOptions.NetworkNames(_options.LocalNetworks), StringComparer.OrdinalIgnoreCase);
        _localFailed = false;
        if (_localUrl is not null && _homeNetworks.Count > 0)
        {
            _networks.Changed += OnNetworksChanged;
        }
    }

    private void StopFollowingNetworks() => _networks.Changed -= OnNetworksChanged;

    private bool IsOnHomeNetwork() =>
        _localUrl is not null && _homeNetworks.Count > 0 && _networks.ConnectedNetworks.Any(_homeNetworks.Contains);

    /// <summary>The address to connect to now: the local one at home, unless it just failed to answer.</summary>
    private Uri ChooseAddress() => IsOnHomeNetwork() && !_localFailed ? _localUrl! : _mainUrl!;

    private void AddressAnswered()
    {
        // Having connected through the main address, the local one gets another chance next time.
        if (_baseUrl == _mainUrl)
        {
            _localFailed = false;
        }
    }

    /// <summary>Returns whether it was the local address that did not answer, so that the main one is due.</summary>
    private bool AddressDidNotAnswer()
    {
        var wasLocal = _localUrl is not null && _baseUrl == _localUrl && _localUrl != _mainUrl;
        _localFailed = wasLocal;
        return wasLocal;
    }

    private void OnNetworksChanged(object? sender, EventArgs e)
    {
        if (_connection is not { } connection || _state != EngineConnectionState.Connected)
        {
            // Not connected: the next attempt chooses anyway.
            return;
        }

        var wanted = IsOnHomeNetwork() ? _localUrl : _mainUrl;
        if (wanted == _baseUrl || Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastSwitch)) < _options.AddressSwitchInterval)
        {
            return;
        }

        Interlocked.Exchange(ref _lastSwitch, Stopwatch.GetTimestamp());
        _localFailed = false;
        LogSwitchingAddress(_logger, wanted!);
        connection.Abort();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "The computer's networks changed; connecting to Home Assistant at {Address} instead.")]
    private static partial void LogSwitchingAddress(ILogger logger, Uri address);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Home Assistant did not answer at its local address {Address} ({Reason}); trying the main address.")]
    private static partial void LogLocalAddressFailed(ILogger logger, Uri address, string reason);
}
