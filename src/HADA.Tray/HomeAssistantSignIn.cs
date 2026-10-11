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
/// <para>
/// The browser comes back to a listener on the loopback interface, at a port Windows picked, which no other
/// computer can reach. Only a request that carries the state handed out with the page finishes the sign-in.
/// </para>
/// <para>
/// The browser is not opened at Home Assistant's page but at that same listener, which sends it on. The page's
/// address has a query string, and Explorer, which is what opens the browser from the administrator copy of the
/// window, takes an address with one for a file to look for.
/// </para>
/// </remarks>
public static class HomeAssistantSignIn
{
    private static readonly TimeSpan WaitForBrowser = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan WaitForRequest = TimeSpan.FromSeconds(10);

    /// <param name="openBrowser">
    /// Opens the address in a browser; see <see cref="OpenInBrowser(Uri)"/>. The address is one on this computer,
    /// without a query string, and can be shown to the user to open by hand should no browser come up.
    /// </param>
    /// <exception cref="TimeoutException">Nobody signed in within five minutes.</exception>
    public static async Task<HomeAssistantSignInResult> RunAsync(Uri baseUrl, Action<Uri> openBrowser, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(openBrowser);

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var signInPage = HomeAssistantAuth.AuthorizeUrl(baseUrl, port, state);

        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        waiting.CancelAfter(WaitForBrowser);

        // Each connection is seen to on its own: a browser opens some it then says nothing on, and those must not
        // keep the one that matters waiting.
        var signedIn = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var accepting = AcceptAsync(listener, state, signInPage, signedIn, waiting.Token);
        try
        {
            openBrowser(HomeAssistantAuth.StartUri(port));
            var code = await signedIn.Task.WaitAsync(waiting.Token);

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var refreshToken = await HomeAssistantAuth.ExchangeCodeAsync(http, baseUrl, HomeAssistantAuth.ClientId(port), code, cancellationToken);
            return new HomeAssistantSignInResult(HomeAssistantAuth.ClientId(port), refreshToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Nobody signed in at Home Assistant in time.");
        }
        finally
        {
            await waiting.CancelAsync();
            listener.Stop();
            await accepting;
        }
    }

    /// <summary>
    /// Opens an address in the user's default browser. From the administrator copy of the window that is left to
    /// the desktop's own Explorer, so that the browser runs as the signed-in user, where their Home Assistant
    /// session is, and not with an administrator's rights.
    /// </summary>
    public static void OpenInBrowser(Uri address) => OpenInBrowser(address, Elevation.IsElevated);

    /// <param name="throughExplorer">
    /// Whether Explorer is asked to open it. It takes plain addresses only: one with a query string it does not
    /// hand to a browser, and opens a window of its own instead.
    /// </param>
    public static void OpenInBrowser(Uri address, bool throughExplorer)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps)
        {
            return;
        }

        if (throughExplorer && address.Query.Length == 0)
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

    private static async Task AcceptAsync(
        TcpListener listener, string state, Uri signInPage, TaskCompletionSource<string> signedIn, CancellationToken cancellationToken)
    {
        var connections = new List<Task>();
        try
        {
            while (true)
            {
                var browser = await listener.AcceptTcpClientAsync(cancellationToken);
                connections.Add(AnswerAsync(browser, state, signInPage, signedIn, cancellationToken));
                connections.RemoveAll(connection => connection.IsCompleted);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // The sign-in is over, one way or the other.
        }

        await Task.WhenAll(connections);
    }

    private static async Task AnswerAsync(
        TcpClient browser, string state, Uri signInPage, TaskCompletionSource<string> signedIn, CancellationToken cancellationToken)
    {
        using var connection = browser;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        request.CancelAfter(WaitForRequest);
        try
        {
            var stream = connection.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(request.Token);

            if (HomeAssistantAuth.IsStart(requestLine))
            {
                // On to Home Assistant's own page, where the signing in is done.
                await WriteAsync(stream, "302 Found", $"Location: {signInPage.AbsoluteUri}\r\n", string.Empty, request.Token);
                return;
            }

            if (!HomeAssistantAuth.TryReadCallback(requestLine, state, out var code))
            {
                // The browser asking for an icon, or somebody else knocking: neither is the sign-in.
                await WriteAsync(stream, "404 Not Found", string.Empty, string.Empty, request.Token);
                return;
            }

            var page = "<!doctype html><meta charset=\"utf-8\"><title>HADA</title><body style=\"font-family:Segoe UI,sans-serif;margin:3em\">"
                + $"<h2>{WebUtility.HtmlEncode(Loc.Get("SignIn_BrowserDone"))}</h2><p>{WebUtility.HtmlEncode(Loc.Get("SignIn_BrowserClose"))}</p></body>";
            await WriteAsync(stream, "200 OK", string.Empty, page, request.Token);
            signedIn.TrySetResult(code);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // A connection that said nothing, or went away: not the sign-in.
        }
    }

    private static async Task WriteAsync(NetworkStream stream, string status, string headers, string page, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(page);
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\n{headers}Content-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}
