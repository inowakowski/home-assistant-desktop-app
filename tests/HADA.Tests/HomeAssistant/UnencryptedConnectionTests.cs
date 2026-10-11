using System.Net;
using HADA.Core;
using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Messaging;
using HADA.Engine.WebSocket;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HADA.Tests.HomeAssistant;

/// <summary>
/// The access token is not sent unencrypted to an address out on the internet, unless that was allowed.
/// The public addresses used here are ones reserved for documentation, which nothing answers on.
/// </summary>
public sealed class UnencryptedConnectionTests
{
    private const string PublicAddress = "http://203.0.113.5:8123";

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("192.168.1.20")]
    [InlineData("169.254.10.10")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.254")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fd00:1::20")]
    [InlineData("::ffff:192.168.1.20")]
    public void Addresses_of_the_computer_and_of_private_networks_are_local(string address) =>
        Assert.True(NetworkScope.IsLocal(IPAddress.Parse(address)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("203.0.113.5")]
    [InlineData("172.32.0.1")]
    [InlineData("100.128.0.1")]
    [InlineData("192.169.1.1")]
    [InlineData("2001:db8::1")]
    [InlineData("::ffff:8.8.8.8")]
    public void Addresses_on_the_internet_are_not(string address) =>
        Assert.False(NetworkScope.IsLocal(IPAddress.Parse(address)));

    [Theory]
    [InlineData("homeassistant", true)]
    [InlineData("homeassistant.local", true)]
    [InlineData("ha.lan", true)]
    [InlineData("ha.home.arpa", true)]
    [InlineData("192.168.1.20", true)]
    [InlineData("[fd00:1::20]", true)]
    [InlineData("example.duckdns.org", false)]
    [InlineData("203.0.113.5", false)]
    [InlineData("localhost.example.com", false)]
    public void A_host_reads_as_local_or_not(string host, bool expected) =>
        Assert.Equal(expected, NetworkScope.LooksLocal(host));

    [Fact]
    public async Task The_test_refuses_an_unencrypted_public_address_without_connecting()
    {
        var result = await HaWebSocketEngine.TestConnectionAsync(
            new HaWebSocketOptions { BaseUrl = PublicAddress, AccessToken = "token" }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("not be encrypted", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Allowed_the_address_is_tried_all_the_same()
    {
        // Cancelled at once: all that matters is that it got as far as connecting, and was not turned away before.
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HaWebSocketEngine.TestConnectionAsync(
            new HaWebSocketOptions { BaseUrl = PublicAddress, AccessToken = "token", AllowUnencrypted = true }, cancelled.Token));
    }

    [Fact]
    public async Task The_engine_does_not_connect_and_says_why()
    {
        await using var bus = new ChannelEventBus();
        await using var engine = new HaWebSocketEngine(
            bus,
            new EntityRegistry(bus),
            Options.Create(new HaWebSocketOptions
            {
                BaseUrl = PublicAddress,
                AccessToken = "token",
                MinReconnectDelay = TimeSpan.FromMilliseconds(50),
            }),
            NullLogger<HaWebSocketEngine>.Instance);

        await engine.StartAsync(CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (engine.Issue != HaWebSocketEngine.IssueUnencrypted)
        {
            await Task.Delay(20, timeout.Token);
        }

        Assert.Equal(EngineConnectionState.Faulted, engine.State);
    }
}
