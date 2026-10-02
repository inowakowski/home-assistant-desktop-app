using HADA.Core;
using HADA.Ipc;
using HADA.Service.Settings;

namespace HADA.Tests.Service;

/// <summary>What tells a portable copy from the installed app, and keeps copies apart.</summary>
public sealed class PortableTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HADA.Tests." + Guid.NewGuid().ToString("N"));

    public PortableTests() => Directory.CreateDirectory(Path.Combine(_root, "tray"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void A_folder_is_a_portable_copy_only_with_the_marker_above_the_program()
    {
        var tray = Path.Combine(_root, "tray");
        Assert.Null(AppInstance.FindPortableRoot(tray));

        File.WriteAllText(Path.Combine(_root, AppInstance.MarkerFileName), "portable");

        Assert.Equal(_root, AppInstance.FindPortableRoot(tray));
        Assert.Equal(_root, AppInstance.FindPortableRoot(tray + Path.DirectorySeparatorChar));

        // The marker counts for the programs one level down, not for the folder it is in or deeper ones.
        Assert.Null(AppInstance.FindPortableRoot(_root));
        Assert.Null(AppInstance.FindPortableRoot(Path.Combine(tray, "pl")));
    }

    [Fact]
    public void The_tests_themselves_run_as_the_installed_app()
    {
        // Everything else in this suite relies on it: default pipe name, no suffix.
        Assert.False(AppInstance.IsPortable);
        Assert.Equal(string.Empty, AppInstance.Suffix);
        Assert.Equal(IpcOptions.DefaultPipeName, AppInstance.PipeName);
        Assert.Equal(IpcOptions.DefaultPipeName, new IpcOptions().PipeName);
    }

    [Fact]
    public void Every_copy_gets_names_of_its_own_however_its_path_is_written()
    {
        var suffix = AppInstance.SuffixFor(_root);

        Assert.Matches(@"^\.P[0-9A-F]{8}$", suffix);
        Assert.Equal(suffix, AppInstance.SuffixFor(_root + Path.DirectorySeparatorChar));
        Assert.Equal(suffix, AppInstance.SuffixFor(_root.ToUpperInvariant()));
        Assert.Equal(suffix, AppInstance.SuffixFor(Path.Combine(_root, "tray", "..")));
        Assert.NotEqual(suffix, AppInstance.SuffixFor(_root + "2"));
    }

    [Fact]
    public void A_portable_copys_settings_live_in_its_own_folder_as_it_is()
    {
        var data = Path.Combine(_root, "data");
        var store = new SettingsStore(data, protectFolder: false);

        Assert.True(store.TryEnsureFolder());
        store.Save(new StoredSettings { DisabledEntities = ["active_window"] });

        Assert.True(File.Exists(Path.Combine(data, "settings.json")));
        Assert.Equal(["active_window"], store.Load().DisabledEntities);

        // Left as the user's folder: permissions still come from the folder above, not from a list of its own.
        Assert.False(new DirectoryInfo(data).GetAccessControl().AreAccessRulesProtected);
    }
}
