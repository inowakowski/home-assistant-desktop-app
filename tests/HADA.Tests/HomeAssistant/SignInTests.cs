using HADA.Core;
using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Messaging;
using HADA.Core.Models;
using HADA.Engine.WebSocket;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HADA.Tests.HomeAssistant;

/// <summary>
/// Signing in through Home Assistant instead of pasting a long-lived token: the code the browser brings back,
/// the refresh token it is exchanged for, and the engine using that to get the access tokens it shows.
/// </summary>
public sealed class SignInTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly FakeHomeAssistant _homeAssistant = new();
    private readonly ChannelEventBus _bus = new();
    private readonly EntityRegistry _registry;

    public SignInTests() => _registry = new EntityRegistry(_bus);

    public async Task InitializeAsync() =>
        await _registry.RegisterAsync(new EntityDescriptor { Id = "cpu_load", Name = "CPU load", Kind = EntityKind.Sensor });

    public async Task DisposeAsync()
    {
        await _homeAssistant.DisposeAsync();
        await _bus.DisposeAsync();
    }

    [Fact]
    public void The_sign_in_page_is_asked_for_under_an_address_of_this_computer()
    {
        var url = HomeAssistantAuth.AuthorizeUrl(new Uri("https://home.example.org/ha"), 50123, "s3cret");

        Assert.Equal("https://home.example.org/ha/auth/authorize", url.GetLeftPart(UriPartial.Path));
        Assert.Contains("response_type=code", url.Query, StringComparison.Ordinal);
        Assert.Contains("client_id=" + Uri.EscapeDataString("http://127.0.0.1:50123/"), url.Query, StringComparison.Ordinal);
        Assert.Contains("redirect_uri=" + Uri.EscapeDataString("http://127.0.0.1:50123/callback"), url.Query, StringComparison.Ordinal);
        Assert.Contains("state=s3cret", url.Query, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("GET /callback?code=abc123&state=s3cret HTTP/1.1", "abc123")]
    [InlineData("GET /callback?state=s3cret&code=a%2Bb HTTP/1.1", "a+b")]
    [InlineData("GET /callback?code=abc123&state=other HTTP/1.1", null)]
    [InlineData("GET /callback?state=s3cret HTTP/1.1", null)]
    [InlineData("GET /favicon.ico HTTP/1.1", null)]
    [InlineData("POST /callback?code=abc123&state=s3cret HTTP/1.1", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Only_the_browser_coming_back_with_the_state_handed_out_finishes_a_sign_in(string? requestLine, string? expected)
    {
        Assert.Equal(expected is not null, HomeAssistantAuth.TryReadCallback(requestLine, "s3cret", out var code));
        Assert.Equal(expected, code);
    }

    [Fact]
    public async Task The_code_is_exchanged_for_a_refresh_token_and_that_for_access_tokens()
    {
        using var http = new HttpClient();

        var refreshToken = await HomeAssistantAuth.ExchangeCodeAsync(
            http, _homeAssistant.BaseUrl, FakeHomeAssistant.ClientId, FakeHomeAssistant.ValidCode, CancellationToken.None);
        var access = await HomeAssistantAuth.RefreshAsync(http, _homeAssistant.BaseUrl, FakeHomeAssistant.ClientId, refreshToken, CancellationToken.None);

        Assert.Equal(FakeHomeAssistant.ValidRefreshToken, refreshToken);
        Assert.Equal(FakeHomeAssistant.ValidToken, access.AccessToken);
        Assert.Equal(TimeSpan.FromMinutes(30), access.ExpiresIn);

        // A code, a token and a client id Home Assistant does not know are refused for good, not for now.
        await Assert.ThrowsAsync<HomeAssistantSignInExpiredException>(() => HomeAssistantAuth.ExchangeCodeAsync(
            http, _homeAssistant.BaseUrl, FakeHomeAssistant.ClientId, "used-up", CancellationToken.None));
        await Assert.ThrowsAsync<HomeAssistantSignInExpiredException>(() => HomeAssistantAuth.RefreshAsync(
            http, _homeAssistant.BaseUrl, "http://127.0.0.1:1/", refreshToken, CancellationToken.None));
    }

    [Fact]
    public async Task The_engine_shows_an_access_token_it_got_for_the_refresh_token_and_keeps_it_while_it_lasts()
    {
        await using var engine = CreateEngine();
        await engine.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => engine.State == EngineConnectionState.Connected);

        var auth = _homeAssistant.SocketMessages[0];
        Assert.Equal(FakeHomeAssistant.ValidToken, auth.GetProperty("access_token").GetString());

        // States are written with the same token, without asking for another each time.
        await _bus.PublishAsync(new TelemetryEvent { SensorId = "cpu_load", State = "1" });
        await _bus.PublishAsync(new TelemetryEvent { SensorId = "cpu_load", State = "2" });
        await WaitUntilAsync(() => Array.Exists(_homeAssistant.StateWrites, write => write.Method == "POST" && write.Body.GetProperty("state").GetString() == "2"));
        Assert.All(
            _homeAssistant.StateWrites.Where(write => write.Method == "POST"),
            write => Assert.Equal("Bearer " + FakeHomeAssistant.ValidToken, write.Authorization));
        Assert.Equal(1, _homeAssistant.TokenRequests);
    }

    [Fact]
    public async Task A_sign_in_that_was_withdrawn_stops_the_engine_and_says_so()
    {
        _homeAssistant.SignInWithdrawn = true;
        await using var engine = CreateEngine();
        await engine.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => engine.Issue == HaWebSocketEngine.IssueSignInExpired);

        // Several reconnect delays' worth of time: it is not tried again, and no connection was made.
        await Task.Delay(400);
        Assert.Equal(EngineConnectionState.Faulted, engine.State);
        Assert.Equal(1, _homeAssistant.TokenRequests);
        Assert.Equal(0, _homeAssistant.ConnectionAttempts);
    }

    [Fact]
    public async Task The_test_signs_in_the_same_way()
    {
        var good = await HaWebSocketEngine.TestConnectionAsync(CreateOptions(), CancellationToken.None);
        Assert.True(good.Success, good.Message);

        _homeAssistant.SignInWithdrawn = true;
        var withdrawn = await HaWebSocketEngine.TestConnectionAsync(CreateOptions(), CancellationToken.None);
        Assert.False(withdrawn.Success);
        Assert.Contains("Sign in again", withdrawn.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_refresh_token_is_not_sent_unencrypted_to_a_public_address()
    {
        var options = CreateOptions();
        options.BaseUrl = "http://203.0.113.5:8123";

        var result = await HaWebSocketEngine.TestConnectionAsync(options, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("not be encrypted", result.Message, StringComparison.Ordinal);
    }

    private HaWebSocketEngine CreateEngine() => new(
        _bus, _registry, Options.Create(CreateOptions()), NullLogger<HaWebSocketEngine>.Instance);

    private HaWebSocketOptions CreateOptions() => new()
    {
        BaseUrl = _homeAssistant.BaseUrl.ToString(),
        AccessToken = FakeHomeAssistant.ValidRefreshToken,
        ClientId = FakeHomeAssistant.ClientId,
        DeviceId = "TestPC",
        DeviceName = "Test PC",
        MinReconnectDelay = TimeSpan.FromMilliseconds(50),
    };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        while (!condition())
        {
            await Task.Delay(20, timeout.Token);
        }
    }
}
