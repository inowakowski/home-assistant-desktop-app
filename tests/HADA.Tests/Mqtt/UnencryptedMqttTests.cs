using System.Net;
using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Messaging;
using HADA.Engine.Mqtt;
using HADA.Ipc;
using HADA.Presentation.Localization;
using HADA.Presentation.ViewModels;
using HADA.Service.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HADA.Tests.Mqtt;

/// <summary>
/// The MQTT password is not sent unencrypted to a broker out on the internet, unless that was allowed: the same
/// rule as for the access token of a Home Assistant connected to directly. The public address used here is one
/// reserved for documentation, which nothing answers on.
/// </summary>
public sealed class UnencryptedMqttTests
{
    private const string PublicAddress = "203.0.113.5";

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("192.168.1.20")]
    [InlineData("10.0.0.5")]
    [InlineData("100.64.0.1")]
    [InlineData("fd00:1::20")]
    public void A_broker_on_a_private_network_is_connected_to_without_TLS(string address) =>
        MqttEngine.EnsureEncryptedOrLocal(new MqttOptions { Host = "broker", Password = "secret" }, IPAddress.Parse(address));

    [Fact]
    public void A_broker_on_the_internet_is_not_unless_TLS_is_on_or_it_was_allowed()
    {
        var address = IPAddress.Parse(PublicAddress);

        var error = Assert.Throws<MqttUnencryptedException>(
            () => MqttEngine.EnsureEncryptedOrLocal(new MqttOptions { Host = "mqtt.example.com" }, address));
        Assert.Contains("mqtt.example.com", error.Message, StringComparison.Ordinal);

        MqttEngine.EnsureEncryptedOrLocal(new MqttOptions { Host = "mqtt.example.com", UseTls = true }, address);
        MqttEngine.EnsureEncryptedOrLocal(new MqttOptions { Host = "mqtt.example.com", AllowUnencrypted = true }, address);
    }

    [Fact]
    public async Task The_test_refuses_an_unencrypted_public_broker_without_connecting()
    {
        var result = await MqttEngine.TestConnectionAsync(
            new MqttOptions { Host = PublicAddress, Username = "user", Password = "secret" }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("TLS is off", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Allowed_the_broker_is_tried_all_the_same()
    {
        // Cancelled at once: all that matters is that it got as far as connecting, and was not turned away before.
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MqttEngine.TestConnectionAsync(
            new MqttOptions { Host = PublicAddress, AllowUnencrypted = true }, cancelled.Token));
    }

    [Fact]
    public async Task The_engine_does_not_connect_and_says_why()
    {
        await using var bus = new ChannelEventBus();
        await using var engine = new MqttEngine(
            bus,
            new EntityRegistry(bus),
            // A long wait before the next attempt: it would set the state back to Connecting, while the issue stays.
            Options.Create(new MqttOptions { Host = PublicAddress, MinReconnectDelay = TimeSpan.FromMinutes(5) }),
            NullLogger<MqttEngine>.Instance);

        await engine.StartAsync(CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (engine.Issue != MqttEngine.IssueUnencrypted)
        {
            await Task.Delay(20, timeout.Token);
        }

        Assert.Equal(EngineConnectionState.Faulted, engine.State);
    }

    [Fact]
    public void The_window_warns_of_a_public_broker_without_TLS_and_offers_to_allow_it()
    {
        var server = MqttServerViewModel.New(() => { });
        server.Host = "mqtt.example.com";

        Assert.True(server.IsUnencryptedPublic);
        Assert.True(server.CanAllowUnencrypted);
        Assert.Equal(Loc.Get("Mqtt_UnencryptedPublic"), server.EncryptionNote);

        server.AllowUnencrypted = true;
        Assert.Equal(Loc.Get("Mqtt_UnencryptedAllowed"), server.EncryptionNote);
        Assert.True(server.ToUpdate().Settings.AllowUnencrypted);

        // TLS settles it, whether switched on or asked for by the address.
        server.UseTls = true;
        Assert.False(server.IsUnencryptedPublic);
        Assert.False(server.HasEncryptionNote);
        server.UseTls = false;
        server.Host = "mqtts://mqtt.example.com";
        Assert.False(server.IsUnencryptedPublic);
        Assert.False(server.HasEncryptionNote);
    }

    [Fact]
    public void A_broker_at_home_gets_a_quiet_note_and_no_switch()
    {
        var server = MqttServerViewModel.New(() => { });
        server.Host = "homeassistant.local";

        Assert.False(server.IsUnencryptedPublic);
        Assert.False(server.CanAllowUnencrypted);
        Assert.Equal(Loc.Get("Mqtt_UnencryptedLocal"), server.EncryptionNote);
    }

    [Fact]
    public void The_permission_is_saved_with_the_server()
    {
        var data = StoredSettingsConfigurationProvider.ToConfiguration(new StoredSettings
        {
            MqttServers =
            [
                new StoredMqttServer(
                    new MqttSettings(PublicAddress, 1883, false, "", "", "", "homeassistant", "hada") { Id = "a", AllowUnencrypted = true },
                    null),
            ],
        });

        Assert.Equal("true", data[$"{MqttServersOptions.SectionName}:{nameof(MqttServersOptions.Items)}:0:{nameof(MqttOptions.AllowUnencrypted)}"]);
    }
}
