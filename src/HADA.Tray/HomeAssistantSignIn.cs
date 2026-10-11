using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using HADA.Core;
using HADA.Tray.Localization;

namespace HADA.Tray;

/// <param name="ClientId">The address the sign-in was made under; the refresh token only works together with it.</param>
/// <param name="RefreshToken">What is saved in place of an access token.</param>
public sealed record HomeAssistantSignInResult(string ClientId, string RefreshToken);

/// <summary>
/// Signs in at Home Assistant through the user's browser: opens Home Assistant's own sign-in page, waits on this
/// computer for the browser to come back with a code, and exchanges the code for a refresh token.
/// </summary>
/// <remarks>
/// The browser comes back to a listener on the loopback interface, at a port Windows picked, which no other
/// computer can reach. Only a request that carries the state handed out with the page finishes the sign-in.
/// </remarks>
public static class HomeAssistantSignIn
{
    private static readonly TimeSpan WaitForBrowser = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan WaitForRequest = TimeSpan.FromSeconds(10);

    /// <param name="openBrowser">Opens the address in a browser; see <see cref="OpenInBrowser"/>.</param>
    /// <exception cref="TimeoutException">Nobody signed in within five minutes.</exception>
    public static async Task<HomeAssistantSignInResult> RunAsync(Uri baseUrl, Action<Uri> openBrowser, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(openBrowser);

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        openBrowser(HomeAssistantAuth.AuthorizeUrl(baseUrl, port, state));

        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        waiting.CancelAfter(WaitForBrowser);
        try
        {
            while (true)
            {
                using var browser = await listener.AcceptTcpClientAsync(waiting.Token);
                if (await TryReadCodeAsync(browser, state, waiting.Token) is not { } code)
                {
                    // The browser asking for an icon, or somebody else knocking: neither is the sign-in.
                    continue;
                }

                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                var refreshToken = await HomeAssistantAuth.ExchangeCodeAsync(http, baseUrl, HomeAssistantAuth.ClientId(port), code, cancellationToken);
                return new HomeAssistantSignInResult(HomeAssistantAuth.ClientId(port), refreshToken);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Nobody signed in at Home Assistant in time.");
        }
    }

    /// <summary>
    /// Opens the address in the user's default browser. From the administrator copy of the window that is left to
    /// the desktop's own Explorer, so that the browser runs as the signed-in user, where their Home Assistant
    /// session is, and not with an administrator's rights.
    /// </summary>
    public static void OpenInBrowser(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps)
        {
            return;
        }

        if (Elevation.IsElevated)
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
            {
                UseShellExecute = false,
            };
            start.ArgumentList.Add(address.AbsoluteUri);
            using var explorer = Process.Start(start);
            return;
        }

        using var process = Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true });
    }

    private static async Task<string?> TryReadCodeAsync(TcpClient browser, string state, CancellationToken cancellationToken)
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        request.CancelAfter(WaitForRequest);
        try
        {
            var stream = browser.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(request.Token);
            var signedIn = HomeAssistantAuth.TryReadCallback(requestLine, state, out var code);

            var page = signedIn
                ? $"<!doctype html><meta charset=\"utf-8\"><title>HADA</title><body style=\"font-family:Segoe UI,sans-serif;margin:3em\"><h2>{WebUtility.HtmlEncode(Loc.Get("SignIn_BrowserDone"))}</h2><p>{WebUtility.HtmlEncode(Loc.Get("SignIn_BrowserClose"))}</p></body>"
                : string.Empty;
            var body = Encoding.UTF8.GetBytes(page);
            var head = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {(signedIn ? "200 OK" : "404 Not Found")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(head, request.Token);
            await stream.WriteAsync(body, request.Token);
            await stream.FlushAsync(request.Token);
            return signedIn ? code : null;
        }
        catch (Exception ex) when (ex is IOException or SocketException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // A connection that said nothing, or went away: not the sign-in.
            return null;
        }
    }
}
