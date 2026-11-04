using HADA.Core;

namespace HADA.Ipc;

public sealed class IpcOptions
{
    public const string DefaultPipeName = "HADA.Session";

    /// <summary>The installed app's pipe, or a portable copy's own; see <see cref="AppInstance"/>.</summary>
    public string PipeName { get; set; } = AppInstance.PipeName;

    /// <summary>Identifies the client in service logs and as <see cref="Core.Models.TelemetryEvent.Source"/>.</summary>
    public string ClientName { get; set; } = "tray";

    public TimeSpan MinReconnectDelay { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxReconnectDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long the server waits before trying again to create a pipe that another process holds.</summary>
    public TimeSpan PipeRetryDelay { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Whether the service and its clients may be processes of the same user: a client of that user may then change
    /// settings without being an elevated administrator, and a client takes a pipe of its own user for the
    /// service's. So it is for a portable copy, and on Linux and macOS, where the service is a process of the user:
    /// whatever its settings can make it do, that user can do anyway. Not for the installed service on Windows,
    /// which runs as SYSTEM.
    /// </summary>
    public bool TrustSameUser { get; set; } = AppInstance.IsPortable || !OperatingSystem.IsWindows();

    /// <summary>How often the server checks whether another user's session came to the front.</summary>
    public TimeSpan SessionCheckInterval { get; set; } = TimeSpan.FromSeconds(2);
}
