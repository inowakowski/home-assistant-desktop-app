using HADA.Ipc;
using HADA.Service.Settings;

namespace HADA.Tests.Service;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "hada-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void Saved_settings_round_trip_and_secrets_are_encrypted_on_disk()
    {
        var store = new SettingsStore(_folder);

        store.Save(new StoredSettings
        {
            Mqtt = new MqttSettings("broker.local", 8883, true, "hada", "", "", "homeassistant", "hada"),
            MqttPassword = SettingsStore.Protect("s3cret-password"),
            DisabledEntities = ["active_window"],
        });
        var loaded = store.Load();

        Assert.Equal("broker.local", loaded.Mqtt?.Host);
        Assert.Equal("s3cret-password", SettingsStore.TryUnprotect(loaded.MqttPassword));
        Assert.Equal(new[] { "active_window" }, loaded.DisabledEntities);
        Assert.DoesNotContain("s3cret-password", File.ReadAllText(store.FilePath));
    }

    [Fact]
    public void Missing_or_damaged_files_load_as_empty_settings()
    {
        var store = new SettingsStore(_folder);
        Assert.Null(store.Load().Mqtt);

        Directory.CreateDirectory(_folder);
        File.WriteAllText(store.FilePath, "{ not json");

        Assert.Null(store.Load().Mqtt);
    }

    [Fact]
    public void Secrets_that_cannot_be_decrypted_are_ignored()
    {
        Assert.Null(SettingsStore.TryUnprotect("not base64!"));
        Assert.Null(SettingsStore.TryUnprotect(Convert.ToBase64String([1, 2, 3, 4])));
        Assert.Null(SettingsStore.TryUnprotect(null));
    }

    [Fact]
    public void Configuration_contains_saved_sections_with_decrypted_secrets()
    {
        var data = StoredSettingsConfigurationProvider.ToConfiguration(new StoredSettings
        {
            HomeAssistant = new HomeAssistantSettings("http://ha.local:8123", "", "Desk", "hada_command"),
            AccessToken = SettingsStore.Protect("token-value"),
            DisabledEntities = ["cpu_load", "active_window"],
        });

        Assert.Equal("http://ha.local:8123", data["HomeAssistant:BaseUrl"]);
        Assert.Equal("token-value", data["HomeAssistant:AccessToken"]);
        Assert.Equal("active_window", data["Entities:Disabled:1"]);
        Assert.False(data.ContainsKey("Mqtt:Host"));
    }

    [Fact]
    public void A_saved_section_without_a_secret_clears_the_secret()
    {
        var data = StoredSettingsConfigurationProvider.ToConfiguration(new StoredSettings
        {
            Mqtt = new MqttSettings("broker.local", 1883, false, "", "", "", "homeassistant", "hada"),
        });

        Assert.Equal(string.Empty, data["Mqtt:Password"]);
    }
}
