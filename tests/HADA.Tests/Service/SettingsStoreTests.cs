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
            MqttServers =
            [
                new StoredMqttServer(
                    new MqttSettings("broker.local", 8883, true, "hada", "", "", "homeassistant", "hada") { Id = "a1", Name = "Mieszkanie" },
                    SettingsStore.Protect("s3cret-password")),
                new StoredMqttServer(
                    new MqttSettings("office.example.com", 1883, false, "", "", "", "homeassistant", "hada") { Id = "b2", Name = "Biuro" },
                    null),
            ],
            DisabledEntities = ["active_window"],
        });
        var loaded = store.Load().GetMqttServers()!;

        Assert.Equal(2, loaded.Count);
        Assert.Equal("broker.local", loaded[0].Settings.Host);
        Assert.Equal("Mieszkanie", loaded[0].Settings.Name);
        Assert.Equal("a1", loaded[0].Settings.Id);
        Assert.Equal("s3cret-password", SettingsStore.TryUnprotect(loaded[0].Password));
        Assert.Null(loaded[1].Password);
        Assert.Equal(new[] { "active_window" }, store.Load().DisabledEntities);
        Assert.DoesNotContain("s3cret-password", File.ReadAllText(store.FilePath));
    }

    [Fact]
    public void The_one_server_saved_by_an_older_version_becomes_the_first_of_the_list_and_keeps_its_password()
    {
        Directory.CreateDirectory(_folder);
        var store = new SettingsStore(_folder);
        File.WriteAllText(store.FilePath, $$"""
            {
              "mqtt": {
                "host": "10.20.9.9",
                "port": 1883,
                "useTls": false,
                "username": "hada",
                "deviceId": "",
                "deviceName": "",
                "discoveryPrefix": "homeassistant",
                "baseTopic": "hada"
              },
              "mqttPassword": "{{SettingsStore.Protect("old-password")}}",
              "disabledEntities": []
            }
            """);

        var server = Assert.Single(store.Load().GetMqttServers()!);
        Assert.Equal("10.20.9.9", server.Settings.Host);
        Assert.Equal("default", server.Settings.Id);
        Assert.Equal(string.Empty, server.Settings.Name);
        Assert.Equal("old-password", SettingsStore.TryUnprotect(server.Password));

        // Saved again, it is written the new way only.
        store.Save(store.Load() with { MqttServers = store.Load().GetMqttServers(), Mqtt = null, MqttPassword = null });
        var text = File.ReadAllText(store.FilePath);
        Assert.Contains("\"mqttServers\"", text);
        Assert.DoesNotContain("\"mqtt\":", text);
        Assert.DoesNotContain("\"mqttPassword\"", text);
        Assert.Equal("old-password", SettingsStore.TryUnprotect(Assert.Single(store.Load().GetMqttServers()!).Password));
    }

    [Fact]
    public void A_folder_made_by_someone_untrusted_is_set_aside_before_anything_in_it_is_read()
    {
        // As if someone had made the folder before HADA was installed, and put settings of their own in it.
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "settings.json"), """{ "customSensors": [ { "name": "Theirs", "type": "PowerShell", "value": "whoami" } ] }""");
        var store = new SettingsStore(_folder);

        // Whoever runs the tests: the user, or the Administrators group when elevated, as on a build server.
        var owner = (System.Security.Principal.SecurityIdentifier)new DirectoryInfo(_folder)
            .GetAccessControl(System.Security.AccessControl.AccessControlSections.Owner)
            .GetOwner(typeof(System.Security.Principal.SecurityIdentifier))!;

        var check = store.SecureFolder(isTrustedOwner: _ => false, takeOwnership: false);

        try
        {
            Assert.NotNull(check.SetAsidePath);
            Assert.True(File.Exists(Path.Combine(check.SetAsidePath, "settings.json")));
            Assert.Contains(owner.Value, check.Owner);
            Assert.True(Directory.Exists(_folder));
            Assert.Empty(Directory.EnumerateFileSystemEntries(_folder));
            Assert.Null(store.Load().CustomSensors);
        }
        finally
        {
            Directory.Delete(check.SetAsidePath!, recursive: true);
        }
    }

    [Fact]
    public void A_folder_of_a_trusted_owner_is_kept_as_it_is()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "settings.json"), """{ "disabledEntities": [ "cpu_load" ] }""");
        var store = new SettingsStore(_folder);

        var check = store.SecureFolder(isTrustedOwner: _ => true, takeOwnership: false);

        Assert.Null(check.SetAsidePath);
        Assert.Equal(["cpu_load"], store.Load().DisabledEntities!);
    }

    [Fact]
    public void A_missing_folder_is_simply_made()
    {
        var store = new SettingsStore(_folder);

        var check = store.SecureFolder(isTrustedOwner: _ => throw new InvalidOperationException("nothing to ask about"), takeOwnership: false);

        Assert.Null(check.SetAsidePath);
        Assert.True(Directory.Exists(_folder));
    }

    [Fact]
    public void The_system_and_the_administrators_may_own_the_folder_and_nobody_else()
    {
        Assert.True(HADA.Service.ServiceHost.IsTrustedOwner(new(System.Security.Principal.WellKnownSidType.LocalSystemSid, null)));
        Assert.True(HADA.Service.ServiceHost.IsTrustedOwner(new(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null)));
        Assert.False(HADA.Service.ServiceHost.IsTrustedOwner(new(System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null)));
        Assert.False(HADA.Service.ServiceHost.IsTrustedOwner(new(System.Security.Principal.WellKnownSidType.WorldSid, null)));
        Assert.False(HADA.Platform.Windows.LocalAdministrators.Contains(new("S-1-5-21-1-2-3-1001")));
    }

    [Fact]
    public void Missing_or_damaged_files_load_as_empty_settings()
    {
        var store = new SettingsStore(_folder);
        Assert.Null(store.Load().GetMqttServers());

        Directory.CreateDirectory(_folder);
        File.WriteAllText(store.FilePath, "{ not json");

        Assert.Null(store.Load().GetMqttServers());
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
        Assert.DoesNotContain(data.Keys, key => key.StartsWith("Mqtt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Each_saved_server_owns_its_password_and_a_server_without_one_clears_it()
    {
        var data = StoredSettingsConfigurationProvider.ToConfiguration(new StoredSettings
        {
            MqttServers =
            [
                new StoredMqttServer(new MqttSettings("a.local", 1883, false, "", "", "", "homeassistant", "hada") { Id = "a", Name = "A" }, null),
                new StoredMqttServer(
                    new MqttSettings("b.local", 1883, false, "", "", "", "homeassistant", "hada") { Id = "b", Name = "B" },
                    SettingsStore.Protect("b-password")),
            ],
        });

        Assert.Equal("2", data["MqttServers:Count"]);
        Assert.Equal("a.local", data["MqttServers:Items:0:Host"]);
        Assert.Equal(string.Empty, data["MqttServers:Items:0:Password"]);
        Assert.Equal("B", data["MqttServers:Items:1:Name"]);
        Assert.Equal("b-password", data["MqttServers:Items:1:Password"]);
    }
}
