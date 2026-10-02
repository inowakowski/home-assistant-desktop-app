using HADA.Ipc;
using HADA.Service;

namespace HADA.Tests.Service;

public class ServiceControlValidationTests
{
    private static readonly MqttSettings Broker = new("broker.local", 1883, false, "hada", "", "", "homeassistant", "hada") { Id = "default" };

    private static readonly SettingsUpdate Valid = new(
        [new MqttServerUpdate(Broker, SecretUpdate.Unchanged)],
        new HomeAssistantSettings("http://homeassistant.local:8123", "", "", "hada_command"),
        SecretUpdate.Unchanged,
        [],
        [ProcessSensor],
        [],
        new UpdateSettings());

    private static CustomSensorDefinition ProcessSensor => new()
    {
        Name = "Gra włączona",
        Type = CustomSensorType.ProcessRunning,
        Value = "game.exe",
    };

    public static TheoryData<string, SettingsUpdate> InvalidSettings => new()
    {
        { "custom sensor without a name", WithCustomSensor(ProcessSensor with { Name = " " }) },
        { "custom sensor without a value", WithCustomSensor(ProcessSensor with { Value = "" }) },
        { "custom sensor with a bad id", WithCustomSensor(ProcessSensor with { Id = "Not An Id" }) },
        { "custom sensor with a built-in id", WithCustomSensor(ProcessSensor with { Id = "cpu_load" }) },
        { "custom sensor with a process path", WithCustomSensor(ProcessSensor with { Value = @"C:\Games\game.exe" }) },
        { "custom sensor polled too often", WithCustomSensor(ProcessSensor with { IntervalSeconds = 0 }) },
        { "custom sensor with the id of a disk sensor", WithCustomSensor(ProcessSensor with { Id = "disk_c_usage" }) },
        { "custom sensor with the id of a built-in button", WithCustomSensor(ProcessSensor with { Id = "shutdown" }) },
        { "custom button without a command", WithCustomSensor(new CustomSensorDefinition { Name = "Backup", Type = CustomSensorType.CommandButton }) },
        { "two custom sensors with one id", Valid with { CustomSensors = [ProcessSensor, ProcessSensor with { Name = "gra wlaczona" }] } },
        { "port 0", WithBroker(Broker with { Port = 0 }) },
        { "port 70000", WithBroker(Broker with { Port = 70000 }) },
        { "host with spaces", WithBroker(Broker with { Host = "not a host" }) },
        { "wildcard topic", WithBroker(Broker with { BaseTopic = "hada/#" }) },
        { "empty discovery prefix", WithBroker(Broker with { DiscoveryPrefix = " / " }) },
        { "ftp url", Valid with { HomeAssistant = Valid.HomeAssistant with { BaseUrl = "ftp://ha.local" } } },
        { "relative url", Valid with { HomeAssistant = Valid.HomeAssistant with { BaseUrl = "ha.local:8123" } } },
        { "event type with space", Valid with { HomeAssistant = Valid.HomeAssistant with { CommandEventType = "hada command" } } },
        { "very long name", WithBroker(Broker with { DeviceName = new string('x', 300) }) },
        { "very long secret", Valid with { MqttServers = [new MqttServerUpdate(Broker, new SecretUpdate(SecretChange.Replace, new string('x', 5000)))] } },
        { "server without an id", WithBroker(Broker with { Id = "" }) },
        { "server id that is no id", WithBroker(Broker with { Id = "Flat 1" }) },
        { "very long server name", WithBroker(Broker with { Name = new string('x', 65) }) },
        { "two servers, one without a name", WithBrokers(Broker with { Name = "Flat" }, Broker with { Id = "b", Host = "office.local" }) },
        { "two servers with one name", WithBrokers(Broker with { Name = "Flat" }, Broker with { Id = "b", Name = "flat", Host = "office.local" }) },
        { "two servers with one id", WithBrokers(Broker with { Name = "Flat" }, Broker with { Name = "Office", Host = "office.local" }) },
        { "one broker twice with one device id", WithBrokers(Broker with { Name = "Flat" }, Broker with { Id = "b", Name = "Again", Host = "BROKER.local" }) },
        { "more servers than allowed", WithBrokers([.. Enumerable.Range(0, ServiceControl.MaxMqttServers + 1).Select(i => Broker with { Id = $"s{i}", Name = $"S{i}", Host = $"h{i}.local" })]) },
    };

    [Fact]
    public void Valid_settings_pass()
    {
        Assert.Null(ServiceControl.Validate(Valid));
    }

    [Fact]
    public void Several_named_servers_pass_even_on_one_broker_with_different_device_ids()
    {
        var servers = WithBrokers(
            Broker with { Name = "Mieszkanie" },
            Broker with { Id = "b2", Name = "Biuro", Host = "office.example.com", Port = 8883, UseTls = true },
            Broker with { Id = "c3", Name = "Same broker, other device", DeviceId = "desk-2" });

        Assert.Null(ServiceControl.Validate(servers));
    }

    [Fact]
    public void Empty_host_and_url_are_allowed_because_they_turn_an_engine_off()
    {
        var disabled = WithBroker(Broker with { Host = "" }) with
        {
            HomeAssistant = Valid.HomeAssistant with { BaseUrl = "" },
        };

        Assert.Null(ServiceControl.Validate(disabled));
        Assert.Null(ServiceControl.Validate(Valid with { MqttServers = [] }));
    }

    [Theory]
    [MemberData(nameof(InvalidSettings))]
    public void Invalid_settings_are_rejected(string description, SettingsUpdate settings)
    {
        Assert.False(ServiceControl.Validate(settings) is null, description);
    }

    [Theory]
    [InlineData("mqtt://broker.local:1884", "broker.local", 1884, false)]
    [InlineData("mqtts://broker.local", "broker.local", null, true)]
    [InlineData(" 10.20.9.9:8883 ", "10.20.9.9", 8883, null)]
    [InlineData("tcp://10.20.9.9/", "10.20.9.9", null, false)]
    [InlineData("[fe80::1]:1883", "fe80::1", 1883, null)]
    [InlineData("fe80::1", "fe80::1", null, null)]
    [InlineData("broker.local", "broker.local", null, null)]
    [InlineData("homeassistant.local", "homeassistant.local", null, null)]
    public void Pasted_broker_addresses_are_split_into_host_port_and_tls(string typed, string host, int? port, bool? useTls)
    {
        Assert.Equal(new MqttAddress(host, port, useTls), MqttAddress.Parse(typed));
        Assert.Null(ServiceControl.Validate(WithBroker(Broker with { Host = typed })));
    }

    [Fact]
    public void A_connection_test_is_not_refused_over_settings_it_does_not_use()
    {
        var unfinished = WithBrokers(Broker with { Name = "Flat" }, Broker with { Id = "b", Host = "not a host" }) with
        {
            HomeAssistant = Valid.HomeAssistant with { BaseUrl = "not a url" },
            CustomSensors = [new CustomSensorDefinition { Name = "Unfinished", Type = CustomSensorType.PowerShell }],
        };

        Assert.NotNull(ServiceControl.Validate(unfinished));
        Assert.Null(ServiceControl.Validate(unfinished, ConnectionTarget.Mqtt, "default"));
        Assert.NotNull(ServiceControl.Validate(unfinished, ConnectionTarget.Mqtt, "b"));
        Assert.NotNull(ServiceControl.Validate(unfinished, ConnectionTarget.Mqtt, "no-such-server"));
        Assert.NotNull(ServiceControl.Validate(unfinished, ConnectionTarget.HomeAssistant));
    }

    [Fact]
    public void A_fixed_text_sensor_ignores_its_interval()
    {
        var text = new CustomSensorDefinition { Name = "Room", Type = CustomSensorType.Text, Value = "Office", IntervalSeconds = 0 };

        Assert.Null(ServiceControl.Validate(WithCustomSensor(text)));
    }

    [Theory]
    [InlineData("Gra włączona", "gra_wlaczona")]
    [InlineData("  CPU temp. (°C) ", "cpu_temp_c")]
    [InlineData("Żółć #1", "zolc_1")]
    [InlineData("!!!", "")]
    public void Custom_sensor_ids_are_derived_from_names(string name, string expectedId)
    {
        Assert.Equal(expectedId, CustomSensorDefinition.ToId(name));
        Assert.Equal(expectedId, new CustomSensorDefinition { Name = name }.Normalize().Id);
    }

    [Fact]
    public void Secrets_never_appear_in_the_string_form_of_an_update()
    {
        var update = Valid with
        {
            AccessToken = new SecretUpdate(SecretChange.Replace, "super-secret-token"),
            MqttServers = [new MqttServerUpdate(Broker, new SecretUpdate(SecretChange.Replace, "broker-password"))],
        };

        Assert.DoesNotContain("super-secret-token", update.ToString());
        Assert.DoesNotContain("broker-password", update.MqttServers[0].ToString());
    }

    [Fact]
    public void New_server_ids_are_valid_ids()
    {
        var id = MqttSettings.NewId();

        Assert.Null(ServiceControl.Validate(WithBroker(Broker with { Id = id })));
        Assert.NotEqual(id, MqttSettings.NewId());
    }

    private static SettingsUpdate WithCustomSensor(CustomSensorDefinition sensor) => Valid with { CustomSensors = [sensor] };

    private static SettingsUpdate WithBroker(MqttSettings broker) => WithBrokers(broker);

    private static SettingsUpdate WithBrokers(params MqttSettings[] brokers) =>
        Valid with { MqttServers = [.. brokers.Select(broker => new MqttServerUpdate(broker, SecretUpdate.Unchanged))] };
}
