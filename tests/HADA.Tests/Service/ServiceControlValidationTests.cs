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
        []);

    public static TheoryData<string, SettingsUpdate> InvalidSettings => new()
    {
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

    [Fact]
    public void Secrets_never_appear_in_the_string_form_of_an_update()
    {
        var update = Valid with { AccessToken = new SecretUpdate(SecretChange.Replace, "super-secret-token") };

        Assert.DoesNotContain("super-secret-token", update.ToString());
    }
}
