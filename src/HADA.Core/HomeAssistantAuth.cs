using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;

namespace HADA.Core;

/// <summary>What Home Assistant gave for a refresh token: an access token, and how long it is good for.</summary>
public sealed record HomeAssistantAccess(string AccessToken, TimeSpan ExpiresIn);

/// <summary>Home Assistant will not take the sign-in any more: it was withdrawn there, or it expired unused.</summary>
public sealed class HomeAssistantSignInExpiredException(string? reason)
    : Exception(reason is { Length: > 0 } ? $"Home Assistant no longer accepts this sign-in ({reason})." : "Home Assistant no longer accepts this sign-in.");

/// <summary>
/// Signing in at Home Assistant the way its own apps do, instead of pasting a long-lived access token: the user
/// signs in on Home Assistant's page in a browser, which hands back a code; the code is exchanged for a refresh
/// token, and the refresh token for a short-lived access token whenever one is needed.
/// </summary>
/// <remarks>
/// <para>
/// Home Assistant tells apps apart by a client id, which is an address. For a program on the user's own computer
/// it is the address of the listener the browser is sent back to, on the loopback interface, where no other
/// computer can reach it; Home Assistant accepts that because the redirect goes to the same address.
/// </para>
/// <para>
/// A refresh token made this way can be withdrawn on its own, in the user's profile in Home Assistant, and it
/// only works together with its client id.
/// </para>
/// </remarks>
public static class HomeAssistantAuth
{
    private const string TokenPath = "auth/token";

    /// <summary>The address HADA listens on for the browser to come back to; the client id is this without the path.</summary>
    public static Uri RedirectUri(int port) => new($"http://127.0.0.1:{port}/callback");

    public static string ClientId(int port) => $"http://127.0.0.1:{port}/";

    /// <summary>The page of Home Assistant on which the user signs in and allows the access.</summary>
    /// <param name="state">Something unguessable that must come back unchanged, so that nobody else can finish the sign-in.</param>
    public static Uri AuthorizeUrl(Uri baseUrl, int port, string state)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        var query = $"response_type=code&client_id={Uri.EscapeDataString(ClientId(port))}"
            + $"&redirect_uri={Uri.EscapeDataString(RedirectUri(port).AbsoluteUri)}&state={Uri.EscapeDataString(state)}";
        return new Uri(new Uri(baseUrl.AbsoluteUri.EndsWith('/') ? baseUrl.AbsoluteUri : baseUrl.AbsoluteUri + "/"), "auth/authorize?" + query);
    }

    /// <summary>
    /// Reads the code out of the request the browser makes when Home Assistant sends it back, e.g.
    /// <c>GET /callback?code=…&amp;state=… HTTP/1.1</c>. Anything else, and a state that is not the one handed
    /// out, is not a sign-in to finish.
    /// </summary>
    public static bool TryReadCallback(string? requestLine, string expectedState, [NotNullWhen(true)] out string? code)
    {
        code = null;
        var parts = requestLine?.Split(' ');
        if (parts is not { Length: >= 2 } || parts[0] != "GET"
            || !Uri.TryCreate("http://127.0.0.1" + parts[1], UriKind.Absolute, out var target)
            || target.AbsolutePath != "/callback")
        {
            return false;
        }

        string? state = null;
        foreach (var pair in target.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var value = Uri.UnescapeDataString(pair[(separator + 1)..].Replace('+', ' '));
            switch (pair[..separator])
            {
                case "code":
                    code = value;
                    break;
                case "state":
                    state = value;
                    break;
            }
        }

        if (code is { Length: > 0 } && state == expectedState)
        {
            return true;
        }

        code = null;
        return false;
    }

    /// <summary>Exchanges the code the browser brought back for a refresh token. The code is good once, and for a short while.</summary>
    public static async Task<string> ExchangeCodeAsync(HttpClient http, Uri baseUrl, string clientId, string code, CancellationToken cancellationToken)
    {
        using var body = await PostAsync(
                http,
                baseUrl,
                new Dictionary<string, string> { ["grant_type"] = "authorization_code", ["code"] = code, ["client_id"] = clientId },
                cancellationToken)
            .ConfigureAwait(false);
        return body.RootElement.TryGetProperty("refresh_token", out var token) && token.GetString() is { Length: > 0 } refreshToken
            ? refreshToken
            : throw new InvalidDataException("Home Assistant answered the sign-in without a refresh token.");
    }

    /// <summary>
    /// Gets an access token for a refresh token. Throws <see cref="HomeAssistantSignInExpiredException"/> when
    /// Home Assistant refuses the refresh token itself, which trying again does not mend.
    /// </summary>
    public static async Task<HomeAssistantAccess> RefreshAsync(
        HttpClient http, Uri baseUrl, string clientId, string refreshToken, CancellationToken cancellationToken)
    {
        using var body = await PostAsync(
                http,
                baseUrl,
                new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken, ["client_id"] = clientId },
                cancellationToken)
            .ConfigureAwait(false);
        var root = body.RootElement;
        if (!root.TryGetProperty("access_token", out var token) || token.GetString() is not { Length: > 0 } accessToken)
        {
            throw new InvalidDataException("Home Assistant answered without an access token.");
        }

        var seconds = root.TryGetProperty("expires_in", out var expires) && expires.TryGetDouble(out var value) && value > 0 ? value : 1800;
        return new HomeAssistantAccess(accessToken, TimeSpan.FromSeconds(seconds));
    }

    private static async Task<JsonDocument> PostAsync(
        HttpClient http, Uri baseUrl, Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(baseUrl);
        using var content = new FormUrlEncodedContent(form);
        using var response = await http.PostAsync(new Uri(baseUrl, TokenPath), content, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            // "invalid_grant" for a token or code Home Assistant does not know (any more), "invalid_request" for a
            // client id that is not the token's.
            throw new HomeAssistantSignInExpiredException(ErrorOf(text));
        }

        response.EnsureSuccessStatusCode();
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Home Assistant's answer to the sign-in was not understood.", ex);
        }
    }

    private static string? ErrorOf(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var description = root.TryGetProperty("error_description", out var detail) ? detail.GetString() : null;
            var error = root.TryGetProperty("error", out var code) ? code.GetString() : null;
            return description is { Length: > 0 } ? description : error;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
