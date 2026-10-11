using System.Diagnostics;
using HADA.Core;
using Microsoft.Extensions.Logging;

namespace HADA.Engine.WebSocket;

/// <summary>
/// The part that knows which token to show Home Assistant. A long-lived access token is shown as it is. After
/// signing in through Home Assistant, what is saved is a refresh token: it is exchanged for an access token that
/// lasts half an hour, and again before that one runs out.
/// </summary>
public sealed partial class HaWebSocketEngine
{
    /// <summary>Home Assistant no longer takes the sign-in: it was withdrawn there, or it expired unused.</summary>
    public const string IssueSignInExpired = "sign_in_expired";

    // An access token is asked for anew while it still has this long to go, so that it never runs out in use.
    private static readonly TimeSpan AccessTokenMargin = TimeSpan.FromMinutes(2);

    // Asked for by the connection and by whatever writes states; one of them asks Home Assistant, the other waits.
    private readonly SemaphoreSlim _accessGate = new(1, 1);
    private string? _accessToken;
    private long _accessTokenUntil;

    private bool SignsIn => NullIfEmpty(_options.ClientId) is not null;

    /// <summary>The token to show Home Assistant now.</summary>
    /// <exception cref="HomeAssistantSignInExpiredException">The sign-in is no longer good; trying again does not mend it.</exception>
    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (!SignsIn)
        {
            return _options.AccessToken!;
        }

        await _accessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_accessToken is { } current && Stopwatch.GetTimestamp() < Interlocked.Read(ref _accessTokenUntil))
            {
                return current;
            }

            // The refresh token opens as much as a password does; it goes nowhere the access token would not.
            await EnsureEncryptedOrLocalAsync(_baseUrl!, _options.AllowUnencrypted, cancellationToken).ConfigureAwait(false);
            var access = await HomeAssistantAuth.RefreshAsync(_http, _baseUrl!, _options.ClientId!, _options.AccessToken!, cancellationToken)
                .ConfigureAwait(false);
            var usable = access.ExpiresIn > AccessTokenMargin + AccessTokenMargin ? access.ExpiresIn - AccessTokenMargin : access.ExpiresIn / 2;
            _accessToken = access.AccessToken;
            Interlocked.Exchange(ref _accessTokenUntil, Stopwatch.GetTimestamp() + (long)(usable.TotalSeconds * Stopwatch.Frequency));
            return access.AccessToken;
        }
        finally
        {
            _accessGate.Release();
        }
    }

    /// <summary>As <see cref="GetAccessTokenAsync"/> for the connection test, which has no engine to keep a token in.</summary>
    private static async Task<string> GetAccessTokenAsync(HaWebSocketOptions options, Uri baseUrl, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.ClientId))
        {
            return options.AccessToken!;
        }

        await EnsureEncryptedOrLocalAsync(baseUrl, options.AllowUnencrypted, cancellationToken).ConfigureAwait(false);
        using var http = new HttpClient { Timeout = HandshakeTimeout };
        return (await HomeAssistantAuth.RefreshAsync(http, baseUrl, options.ClientId, options.AccessToken!, cancellationToken).ConfigureAwait(false))
            .AccessToken;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "{Reason} Sign in again on the Connections page. The WebSocket engine will not retry until then.")]
    private static partial void LogSignInExpired(ILogger logger, string reason);
}
