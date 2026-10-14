using HADA.Core;
using HADA.Service.Settings;

namespace HADA.Tests.Hosting;

public sealed class AppFoldersTests
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    [WindowsFact]
    public void On_windows_the_folders_are_those_hada_always_used()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.Equal(Path.Combine(programData, "HADA"), AppFolders.ServiceData);
        Assert.Equal(Path.Combine(programData, "HADA", "logs"), AppFolders.ServiceLogs);
        Assert.Equal(Path.Combine(localAppData, "HADA"), AppFolders.UserData);
        Assert.Equal(Path.Combine(localAppData, "HADA", "logs"), AppFolders.UserLogs);
    }

    [Fact]
    public void On_linux_the_folders_follow_the_xdg_base_directories()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        Assert.Equal(Path.Combine(Base("XDG_CONFIG_HOME", ".config"), "hada"), AppFolders.ServiceData);
        Assert.Equal(Path.Combine(Base("XDG_DATA_HOME", ".local/share"), "hada"), AppFolders.UserData);
        Assert.Equal(Path.Combine(Base("XDG_STATE_HOME", ".local/state"), "hada", "logs"), AppFolders.ServiceLogs);
        Assert.Equal(AppFolders.ServiceLogs, AppFolders.UserLogs);
    }

    [Fact]
    public void On_macos_the_folders_are_those_of_the_users_library()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        Assert.Equal(Path.Combine(Home, "Library", "Application Support", "HADA"), AppFolders.ServiceData);
        Assert.Equal(AppFolders.ServiceData, AppFolders.UserData);
        Assert.Equal(Path.Combine(Home, "Library", "Logs", "HADA"), AppFolders.ServiceLogs);
        Assert.Equal(AppFolders.ServiceLogs, AppFolders.UserLogs);
    }

    [Fact]
    public void A_settings_folder_given_by_a_test_keeps_its_logs_inside()
    {
        var folder = Path.Combine(Path.GetTempPath(), "hada-folders-test");

        Assert.Equal(Path.Combine(folder, "logs"), new SettingsStore(folder).LogFolder);
        Assert.Equal(AppFolders.ServiceLogs, new SettingsStore().LogFolder);
    }

    [Fact]
    public void A_private_folder_is_the_users_alone_where_the_system_has_such_permissions()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"hada-private-{Guid.NewGuid():N}", "logs");
        try
        {
            AppFolders.CreatePrivate(folder);

            Assert.True(Directory.Exists(folder));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(folder));
            }
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true);
        }
    }

    private static string Base(string variable, string defaultInHome) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value && Path.IsPathRooted(value)
            ? value
            : Path.Combine(Home, defaultInHome);
}
