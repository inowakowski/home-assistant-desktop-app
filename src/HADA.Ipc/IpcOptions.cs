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
    /// Whether a client running as the same user as the server may change settings without being an elevated
    /// administrator. For a portable copy, whose service is a process of the user: whatever its settings can make
    /// it do, that user can do anyway.
    /// </summary>
    public bool TrustSameUser { get; set; }

    /// <summary>How often the server checks whether another user's session came to the front.</summary>
    public TimeSpan SessionCheckInterval { get; set; } = TimeSpan.FromSeconds(2);
}
