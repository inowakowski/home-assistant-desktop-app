using HADA.Engine.WebSocket;
using HADA.Service.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace HADA.Tests.Service;

public sealed class MobileAppRegistrationStoreTests : IDisposable
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
    public void A_registration_is_found_again_by_another_store_on_the_same_folder()
    {
        CreateStore().Save("home", new MobileAppRegistration("secret-webhook-id", "http://ha.local:8123/", "desk"));

        var loaded = CreateStore().Load("home");

        Assert.Equal(new MobileAppRegistration("secret-webhook-id", "http://ha.local:8123/", "desk"), loaded);
        Assert.Null(CreateStore().Load("office"));
    }

    [Fact]
    public void The_webhook_id_is_not_written_as_it_is()
    {
        var store = CreateStore();
        store.Save("home", new MobileAppRegistration("secret-webhook-id", "http://ha.local:8123/", "desk"));

        Assert.DoesNotContain("secret-webhook-id", File.ReadAllText(store.FilePath));
    }

    [Fact]
    public void Registrations_of_servers_that_were_removed_are_forgotten()
    {
        var store = CreateStore();
        store.Save("home", new MobileAppRegistration("a", "http://home.local:8123/", "desk"));
        store.Save("office", new MobileAppRegistration("b", "http://office.local:8123/", "desk"));

        store.KeepOnly(["office", "new"]);

        Assert.Null(store.Load("home"));
        Assert.Equal("b", store.Load("office")?.WebhookId);

        store.Save("office", null);
        Assert.Null(CreateStore().Load("office"));
    }

    [Fact]
    public void A_missing_or_damaged_file_means_no_registration()
    {
        var store = CreateStore();
        Assert.Null(store.Load("home"));

        Directory.CreateDirectory(_folder);
        File.WriteAllText(store.FilePath, "{ not json");

        Assert.Null(store.Load("home"));
        store.KeepOnly([]);
    }

    private MobileAppRegistrationStore CreateStore() =>
        new(new SettingsStore(_folder, protectFolder: false), NullLogger<MobileAppRegistrationStore>.Instance);
}
