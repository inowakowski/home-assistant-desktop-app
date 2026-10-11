using HADA.Core;

namespace HADA.Tests.Entities;

/// <summary>Which dashboards the tray icon's menu offers: one per Home Assistant connected to directly, and the user's own.</summary>
public sealed class DashboardsTests
{
    private static readonly (string Id, string Name, string Url)[] TwoServers =
    [
        ("home", "Home", "https://home.example.org"),
        ("office", "", "http://192.168.5.10:8123/"),
    ];

    [Fact]
    public void Every_server_gets_a_dashboard_named_after_it_or_its_host()
    {
        var own = string.Empty;
        var servers = Dashboards.Sync([], TwoServers, ref own);

        var dashboards = Dashboards.List(servers, own);

        Assert.Equal(["ha:home", "ha:office"], dashboards.Select(dashboard => dashboard.Id));
        Assert.Equal(["Home", "192.168.5.10"], dashboards.Select(dashboard => dashboard.Name));
        Assert.Equal("https://home.example.org/", dashboards[0].Address.AbsoluteUri);
    }

    [Fact]
    public void What_the_user_chose_for_a_server_stays_while_its_name_and_address_follow()
    {
        var own = string.Empty;
        var servers = Dashboards.Sync([], TwoServers, ref own);
        servers = [servers[0] with { OwnUrl = "https://home.example.org/lovelace/desk" }, servers[1] with { Show = false }];

        servers = Dashboards.Sync(
            servers, [("home", "Flat", "https://flat.example.org"), ("office", "Office", "http://192.168.5.10:8123")], ref own);

        Assert.Equal("Flat", servers[0].Name);
        Assert.Equal("https://flat.example.org/", servers[0].Url);
        Assert.Equal("https://home.example.org/lovelace/desk", servers[0].OwnUrl);
        Assert.False(servers[1].Show);

        // Only what is switched on is offered, at the address chosen for it.
        var dashboard = Assert.Single(Dashboards.List(servers, own));
        Assert.Equal("https://home.example.org/lovelace/desk", dashboard.Address.AbsoluteUri);
    }

    [Fact]
    public void A_server_that_is_gone_is_dropped_and_one_without_an_address_is_not_listed()
    {
        var own = string.Empty;
        var servers = Dashboards.Sync([], TwoServers, ref own);

        servers = Dashboards.Sync(servers, [("office", "", "http://192.168.5.10:8123"), ("new", "New", "")], ref own);

        Assert.Equal("office", Assert.Single(servers).Id);
    }

    [Fact]
    public void An_address_typed_before_becomes_the_own_address_of_the_server_it_is_at()
    {
        var own = "https://home.example.org/lovelace/desk";

        var servers = Dashboards.Sync([], TwoServers, ref own);

        Assert.Equal("https://home.example.org/lovelace/desk", servers[0].OwnUrl);
        Assert.Equal(string.Empty, own);
        Assert.Equal(2, Dashboards.List(servers, own).Count);
    }

    [Fact]
    public void An_address_at_no_server_stays_a_dashboard_of_its_own()
    {
        var own = "http://homeassistant.local:8123/lovelace/desk";

        var servers = Dashboards.Sync([], TwoServers, ref own);
        var dashboards = Dashboards.List(servers, own);

        Assert.Equal("http://homeassistant.local:8123/lovelace/desk", own);
        Assert.Equal([Dashboards.OwnId, "homeassistant.local"], [dashboards[^1].Id, dashboards[^1].Name]);
        Assert.Equal(3, dashboards.Count);
    }

    [Fact]
    public void An_own_address_a_server_already_shows_is_not_offered_twice()
    {
        var own = string.Empty;
        var servers = Dashboards.Sync([], TwoServers, ref own);

        Assert.Equal(2, Dashboards.List(servers, "https://home.example.org/").Count);
    }

    [Fact]
    public void The_list_survives_being_written_down_and_read_back()
    {
        var own = string.Empty;
        var servers = Dashboards.Sync([], TwoServers, ref own);
        servers = [servers[0] with { OwnUrl = "https://home.example.org/lovelace/desk" }, servers[1] with { Show = false }];

        Assert.Equal(servers, Dashboards.Parse(Dashboards.Serialize(servers)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"id\":\"home\"}")]
    [InlineData("[{\"name\":\"no id\"}]")]
    public void A_list_that_is_missing_or_damaged_is_empty(string? json) => Assert.Empty(Dashboards.Parse(json));

    [Theory]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("javascript:alert(1)")]
    [InlineData("homeassistant.local:8123")]
    public void Only_http_and_https_addresses_are_shown(string address)
    {
        Assert.False(Dashboards.TryGetAddress(address, out _));
        Assert.Empty(Dashboards.List([new ServerDashboard("home", "Home", address)], address));
    }
}
