namespace HADA.Service.Settings;

/// <summary>Says in the log what <see cref="SettingsStore.SecureFolder"/> found, once logging is there to say it.</summary>
public sealed partial class SettingsFolderReport(FolderCheck check, SettingsStore store, ILogger<SettingsFolderReport> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (check.SetAsidePath is { } setAside)
        {
            LogSetAside(logger, store.FolderPath, check.Owner ?? "someone else", setAside);
        }
        else if (check.OwnershipFailures > 0)
        {
            LogOwnershipFailed(logger, check.OwnershipFailures, store.FolderPath);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Error, Message = "The folder {Folder} belonged to {Owner}, not to the system or an administrator, so what was in it was not trusted: it was moved to {SetAside}, and HADA starts without settings. Set it up again; an administrator can look at the old folder and delete it.")]
    private static partial void LogSetAside(ILogger logger, string folder, string owner, string setAside);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Count} files in {Folder} could not be given to the Administrators group.")]
    private static partial void LogOwnershipFailed(ILogger logger, int count, string folder);
}
