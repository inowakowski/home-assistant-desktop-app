namespace HADA.Ipc;

public sealed class IpcOptions
{
    public const string DefaultPipeName = "HADA.Session";

    public string PipeName { get; set; } = DefaultPipeName;

    /// <summary>Identifies the client in service logs and as <see cref="Core.Models.TelemetryEvent.Source"/>.</summary>
    public string ClientName { get; set; } = "tray";

    public TimeSpan MinReconnectDelay { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxReconnectDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long the server waits before trying again to create a pipe that another process holds.</summary>
    public TimeSpan PipeRetryDelay { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How often the server checks whether another user's session came to the front.</summary>
    public TimeSpan SessionCheckInterval { get; set; } = TimeSpan.FromSeconds(2);
}
