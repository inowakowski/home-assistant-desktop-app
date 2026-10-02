using System.Net;
using System.Net.Sockets;
using HADA.Engine.Mqtt;

namespace HADA.Tests.Mqtt;

/// <summary>Turning the broker's name into an address that answers, as names ending in .local need.</summary>
public class BrokerLocatorTests
{
    private static readonly IPAddress V4 = IPAddress.Parse("192.168.1.1");
    private static readonly IPAddress V6 = IPAddress.Parse("fd00:1::1");
    private static readonly IPAddress OtherV4 = IPAddress.Parse("192.168.1.5");

    [Fact]
    public async Task An_IP_address_is_used_as_it_is()
    {
        var locator = new BrokerLocator(
            (_, _) => throw new InvalidOperationException("must not look anything up"),
            (_, _) => throw new InvalidOperationException("must not try anything"));

        var location = await locator.LocateAsync("192.168.1.9", 1883, CancellationToken.None);

        Assert.Equal(IPAddress.Parse("192.168.1.9"), location.Address);
    }

    [Fact]
    public async Task A_name_with_one_address_is_not_tried_beforehand()
    {
        var locator = new BrokerLocator((_, _) => Task.FromResult(new[] { V4 }), (_, _) => throw new InvalidOperationException());

        Assert.Equal(V4, (await locator.LocateAsync("broker.local", 1883, CancellationToken.None)).Address);
    }

    [Fact]
    public void IPv4_comes_before_IPv6_and_the_address_that_worked_before_both()
    {
        Assert.Equal([V4, OtherV4, V6], BrokerLocator.Order([V6, V4, OtherV4, V4], preferred: null));
        Assert.Equal([V6, V4], BrokerLocator.Order([V4, V6], preferred: V6));
    }

    [Fact]
    public async Task Of_several_addresses_the_first_that_accepts_a_connection_is_taken_and_remembered()
    {
        var tried = new List<IPAddress>();
        var locator = new BrokerLocator(
            (_, _) => Task.FromResult(new[] { V6, V4, OtherV4 }),
            (endPoint, _) =>
            {
                tried.Add(endPoint.Address);
                return endPoint.Address.Equals(OtherV4) ? Task.CompletedTask : throw new SocketException((int)SocketError.ConnectionRefused);
            });

        var location = await locator.LocateAsync("homeassistant.local", 1883, CancellationToken.None);

        Assert.Equal(OtherV4, location.Address);
        Assert.Equal([V4, OtherV4], tried);
        Assert.Equal([V4, OtherV4, V6], location.Candidates);

        // Next time it is asked first.
        tried.Clear();
        await locator.LocateAsync("homeassistant.local", 1883, CancellationToken.None);
        Assert.Equal([OtherV4], tried);
    }

    [Fact]
    public async Task An_address_that_does_not_answer_is_given_up_on_and_the_next_one_tried()
    {
        var locator = new BrokerLocator(
            (_, _) => Task.FromResult(new[] { V4, V6 }),
            (endPoint, cancellationToken) => endPoint.Address.Equals(V4) ? Task.Delay(Timeout.Infinite, cancellationToken) : Task.CompletedTask)
        {
            ConnectTimeout = TimeSpan.FromMilliseconds(200),
        };

        Assert.Equal(V6, (await locator.LocateAsync("homeassistant.local", 1883, CancellationToken.None)).Address);
    }

    [Fact]
    public async Task When_no_address_answers_the_message_says_what_each_one_did()
    {
        var locator = new BrokerLocator(
            (_, _) => Task.FromResult(new[] { V4, V6 }),
            (endPoint, cancellationToken) => endPoint.Address.Equals(V4)
                ? throw new SocketException((int)SocketError.ConnectionRefused)
                : Task.Delay(Timeout.Infinite, cancellationToken))
        {
            ConnectTimeout = TimeSpan.FromMilliseconds(100),
        };

        var error = await Assert.ThrowsAsync<BrokerNotFoundException>(() => locator.LocateAsync("homeassistant.local", 1883, CancellationToken.None));

        Assert.Contains("192.168.1.1 refused the connection", error.Message);
        Assert.Contains("fd00:1::1 did not answer", error.Message);
        Assert.Contains("port 1883", error.Message);
    }

    [Fact]
    public async Task A_name_that_is_not_found_says_so_and_explains_local_names()
    {
        var locator = new BrokerLocator((_, _) => throw new SocketException((int)SocketError.HostNotFound));

        var error = await Assert.ThrowsAsync<BrokerNotFoundException>(() => locator.LocateAsync("homeassistant.local", 1883, CancellationToken.None));

        Assert.Contains("'homeassistant.local' could not be looked up", error.Message);
        Assert.Contains("mDNS", error.Message);
        Assert.DoesNotContain("mDNS", (await Assert.ThrowsAsync<BrokerNotFoundException>(
            () => locator.LocateAsync("broker.example.com", 1883, CancellationToken.None))).Message);
    }

    [Fact]
    public async Task A_lookup_that_takes_too_long_counts_as_failed()
    {
        var locator = new BrokerLocator(
            async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return [];
            })
        {
            ResolveTimeout = TimeSpan.FromMilliseconds(100),
        };

        var error = await Assert.ThrowsAsync<BrokerNotFoundException>(() => locator.LocateAsync("slow.local", 1883, CancellationToken.None));
        Assert.Contains("no answer", error.Message);
    }

    [Fact]
    public async Task A_name_that_cannot_be_looked_up_for_a_moment_leads_where_it_led_before()
    {
        var lookups = 0;
        var locator = new BrokerLocator(
            (_, _) => ++lookups == 1 ? Task.FromResult(new[] { V4 }) : throw new SocketException((int)SocketError.HostNotFound));

        var first = await locator.LocateAsync("homeassistant.local", 1883, CancellationToken.None);
        locator.Remember(first.Address);
        var second = await locator.LocateAsync("homeassistant.local", 1883, CancellationToken.None);

        Assert.Equal(V4, second.Address);
        Assert.True(second.IsRemembered);
    }

    [Fact]
    public async Task A_real_name_with_several_addresses_reaches_the_one_that_listens()
    {
        // 127.0.0.2 is this computer too, but nothing listens there; the listener is on 127.0.0.1 only.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var locator = new BrokerLocator((_, _) => Task.FromResult(new[] { IPAddress.Parse("127.0.0.2"), IPAddress.Loopback }));

        var location = await locator.LocateAsync("broker.test", port, CancellationToken.None);

        Assert.Equal(IPAddress.Loopback, location.Address);
    }
}
