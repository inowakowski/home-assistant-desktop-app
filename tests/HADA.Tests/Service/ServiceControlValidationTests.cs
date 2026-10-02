using HADA.Ipc;
using HADA.Service;

namespace HADA.Tests.Service;

public class ServiceControlValidationTests
{
    private static readonly SettingsUpdate Valid = new(
        new MqttSettings("broker.local", 1883, false, "hada", "", "", "homeassistant", "hada"),
        SecretUpdate.Unchanged,
        new HomeAssistantSettings("http://homeassistant.local:8123", "", "", "hada_command"),
        SecretUpdate.Unchanged,
        [],
        [ProcessSensor],
        []);

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
        { "port 0", Valid with { Mqtt = Valid.Mqtt with { Port = 0 } } },
        { "port 70000", Valid with { Mqtt = Valid.Mqtt with { Port = 70000 } } },
        { "host with spaces", Valid with { Mqtt = Valid.Mqtt with { Host = "not a host" } } },
        { "wildcard topic", Valid with { Mqtt = Valid.Mqtt with { BaseTopic = "hada/#" } } },
        { "empty discovery prefix", Valid with { Mqtt = Valid.Mqtt with { DiscoveryPrefix = " / " } } },
        { "ftp url", Valid with { HomeAssistant = Valid.HomeAssistant with { BaseUrl = "ftp://ha.local" } } },
        { "relative url", Valid with { HomeAssistant = Valid.HomeAssistant with { BaseUrl = "ha.local:8123" } } },
        { "event type with space", Valid with { HomeAssistant = Valid.HomeAssistant with { CommandEventType = "hada command" } } },
        { "very long name", Valid with { Mqtt = Valid.Mqtt with { DeviceName = new string('x', 300) } } },
        { "very long secret", Valid with { MqttPassword = new SecretUpdate(SecretChange.Replace, new string('x', 5000)) } },
    };

    [Fact]
    public void Valid_settings_pass()
    {
        Assert.Null(ServiceControl.Validate(Valid));
    }

    [Fact]
    public void Empty_host_and_url_are_allowed_because_they_turn_an_engine_off()
    {
        var disabled = Valid with
        {
            Mqtt = Valid.Mqtt with { Host = "" },
            HomeAssistant = Valid.HomeAssistant with { BaseUrl = "" },
        };

        Assert.Null(ServiceControl.Validate(disabled));
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
    public void Pasted_broker_addresses_are_split_into_host_port_and_tls(string typed, string host, int? port, bool? useTls)
    {
        Assert.Equal(new MqttAddress(host, port, useTls), MqttAddress.Parse(typed));
        Assert.Null(ServiceControl.Validate(Valid with { Mqtt = Valid.Mqtt with { Host = typed } }));
    }

    [Fact]
    public void A_connection_test_is_not_refused_over_settings_it_does_not_use()
    {
        var unfinished = Valid with
        {
            HomeAssistant = Valid.HomeAssistant with { BaseUrl = "not a url" },
            CustomSensors = [new CustomSensorDefinition { Name = "Unfinished", Type = CustomSensorType.PowerShell }],
        };

        Assert.NotNull(ServiceControl.Validate(unfinished));
        Assert.Null(ServiceControl.Validate(unfinished, ConnectionTarget.Mqtt));
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

    private static SettingsUpdate WithCustomSensor(CustomSensorDefinition sensor) => Valid with { CustomSensors = [sensor] };

    [Fact]
    public void Secrets_never_appear_in_the_string_form_of_an_update()
    {
        var update = Valid with { AccessToken = new SecretUpdate(SecretChange.Replace, "super-secret-token") };

        Assert.DoesNotContain("super-secret-token", update.ToString());
    }
}
