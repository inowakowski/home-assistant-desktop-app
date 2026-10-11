using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Messaging;
using HADA.Engine.WebSocket;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HADA.Tests.HomeAssistant;

/// <summary>
/// A Home Assistant with two addresses: the main one, and a local one for the networks named as home. Each
/// address is played by a Home Assistant of its own here, which tells which one the engine went to.
/// </summary>
public sealed class HomeNetworkTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly FakeHomeAssistant _main = new();
    private readonly FakeHomeAssistant _local = new();
    private readonly FakeNetworks _networks = new();
    private readonly InMemoryMobileAppRegistrationStore _registrations = new();
    private readonly ChannelEventBus _bus = new();
    private readonly EntityRegistry _registry;

    public HomeNetworkTests() => _registry = new EntityRegistry(_bus);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _main.DisposeAsync();
        await _local.DisposeAsync();
        await _bus.DisposeAsync();
    }

    [Fact]
    public async Task At_home_the_local_address_is_used()
    {
        _networks.ConnectedNetworks = ["Office VPN", "home wifi"];
        await using var engine = CreateEngine();
        await engine.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => engine.State == EngineConnectionState.Connected);

        // Names are told apart as Windows tells them: without regard to case.
        Assert.Equal(1, _local.ConnectionAttempts);
        Assert.Equal(0, _main.ConnectionAttempts);
    }

    [Fact]
    public async Task Away_the_local_address_is_not_tried_at_all()
    {
        _networks.ConnectedNetworks = ["Cafe"];
        await using var engine = CreateEngine();
        await engine.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => engine.State == EngineConnectionState.Connected);

        Assert.Equal(1, _main.ConnectionAttempts);
        Assert.Equal(0, _local.ConnectionAttempts);
    }

    [Fact]
    public async Task Without_a_network_named_the_local_address_is_never_used()
    {
        _networks.ConnectedNetworks = ["Home Wifi"];
        await using var engine = CreateEngine(homeNetworks: "");
        await engine.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => engine.State == EngineConnectionState.Connected);

        Assert.Equal(0, _local.ConnectionAttempts);
    }

    [Fact]
    public async Task Coming_home_and_leaving_again_change_the_address()
    {
        _networks.ConnectedNetworks = ["Cafe"];
        await using var engine = CreateEngine();
        await engine.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => engine.State == EngineConnectionState.Connected && _main.ConnectionAttempts == 1);

        _networks.Change("Home Wifi");
        await WaitUntilAsync(() => _local.ConnectionAttempts == 1 && engine.State == EngineConnectionState.Connected);

        _networks.Change("Cafe");
        await WaitUntilAsync(() => _main.ConnectionAttempts == 2 && engine.State == EngineConnectionState.Connected);
        Assert.Equal(1, _local.ConnectionAttempts);
    }

    [Fact]
    public async Task A_change_of_networks_that_leaves_the_address_as_it_is_does_not_disturb_the_connection()
    {
        _networks.ConnectedNetworks = ["Cafe"];
        await using var engine = CreateEngine();
        await engine.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => engine.State == EngineConnectionState.Connected);

        _networks.Change("Cafe", "Hotel");
        await Task.Delay(300);

        Assert.Equal(EngineConnectionState.Connected, engine.State);
        Assert.Equal(1, _main.ConnectionAttempts);
    }

    [Fact]
    public async Task A_local_address_that_does_not_answer_is_followed_by_the_main_one()
    {
        _networks.ConnectedNetworks = ["Home Wifi"];
        _local.IsDown = true;
        await using var engine = CreateEngine();
        await engine.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => engine.State == EngineConnectionState.Connected);

        // Tried first, and then the main one at once.
        Assert.Equal(1, _local.ConnectionAttempts);
        Assert.Equal(1, _main.ConnectionAttempts);

        // The next time it is connected, the local address gets its chance again.
        _local.IsDown = false;
        _networks.Change("Cafe");
        await WaitUntilAsync(() => _main.ConnectionAttempts == 1 && engine.State == EngineConnectionState.Connected);
        _networks.Change("Home Wifi");
        await WaitUntilAsync(() => _local.ConnectionAttempts == 2 && engine.State == EngineConnectionState.Connected);
    }

    [Fact]
    public async Task The_registration_is_that_of_the_main_address_whichever_was_used_to_make_it()
    {
        _networks.ConnectedNetworks = ["Home Wifi"];
        await using var engine = CreateEngine(notifications: true);
        await engine.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => _local.HasPushChannel);

        // Made through the local address, and still not a registration to make again from elsewhere.
        Assert.Equal(_main.BaseUrl.AbsoluteUri, _registrations.Load("home")!.BaseUrl);
    }

    [Fact]
    public async Task The_test_tries_both_addresses_and_says_which_is_which()
    {
        var options = CreateOptions(notifications: false, homeNetworks: "Home Wifi");
        var both = await HaWebSocketEngine.TestConnectionAsync(options, CancellationToken.None);

        Assert.True(both.Success, both.Message);
        Assert.Contains("Main address:", both.Message, StringComparison.Ordinal);
        Assert.Contains("Local address:", both.Message, StringComparison.Ordinal);

        _local.IsDown = true;
        var one = await HaWebSocketEngine.TestConnectionAsync(options, CancellationToken.None);
        Assert.False(one.Success);
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("Home\r\n\r\n  Office VPN \nhome", 2)]
    public void Networks_are_named_one_per_line(string? networks, int expected) =>
        Assert.Equal(expected, HaWebSocketOptions.NetworkNames(networks).Count);

    private HaWebSocketEngine CreateEngine(bool notifications = false, string homeNetworks = "Home Wifi") => new(
        _bus,
        _registry,
        Options.Create(CreateOptions(notifications, homeNetworks)),
        NullLogger<HaWebSocketEngine>.Instance,
        filter: null,
        _registrations,
        _networks);

    private HaWebSocketOptions CreateOptions(bool notifications, string homeNetworks) => new()
    {
        Id = "home",
        Name = "Home",
        BaseUrl = _main.BaseUrl.ToString(),
        LocalUrl = _local.BaseUrl.ToString(),
        LocalNetworks = homeNetworks,
        AccessToken = FakeHomeAssistant.ValidToken,
        DeviceId = "TestPC",
        DeviceName = "Test PC",
        Notifications = notifications,
        SensorMode = HomeAssistantSensorMode.Off,
        AddressSwitchInterval = TimeSpan.Zero,
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

    private sealed class FakeNetworks : INetworkLocation
    {
        public IReadOnlyList<string> ConnectedNetworks { get; set; } = [];

        public event EventHandler? Changed;

        public void Change(params string[] networks)
        {
            ConnectedNetworks = networks;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
