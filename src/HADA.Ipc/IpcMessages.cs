using System.Text.Json.Serialization;
using HADA.Core.Entities;
using HADA.Core.Models;

namespace HADA.Ipc;

[JsonPolymorphic]
[JsonDerivedType(typeof(HelloMessage), "hello")]
[JsonDerivedType(typeof(EntityRegistrationMessage), "entity")]
[JsonDerivedType(typeof(TelemetryMessage), "telemetry")]
[JsonDerivedType(typeof(GetStatusRequest), "getStatus")]
[JsonDerivedType(typeof(StatusResponse), "status")]
[JsonDerivedType(typeof(GetSettingsRequest), "getSettings")]
[JsonDerivedType(typeof(SettingsResponse), "settings")]
[JsonDerivedType(typeof(SaveSettingsRequest), "saveSettings")]
[JsonDerivedType(typeof(TestConnectionRequest), "testConnection")]
[JsonDerivedType(typeof(OperationResponse), "operation")]
[JsonDerivedType(typeof(GetLogsRequest), "getLogs")]
[JsonDerivedType(typeof(LogsResponse), "logs")]
[JsonDerivedType(typeof(ErrorResponse), "error")]
public abstract record IpcMessage;

/// <summary>First message from a client. The server drops clients speaking another protocol version.</summary>
public sealed record HelloMessage(int ProtocolVersion, string ClientName, IpcClientRole Role = IpcClientRole.Sensors) : IpcMessage;

public sealed record EntityRegistrationMessage(EntityDescriptor Entity) : IpcMessage;

public sealed record TelemetryMessage(TelemetryEvent Reading) : IpcMessage;

/// <summary>A request from a control client; the matching response carries the same id.</summary>
public abstract record IpcRequest(Guid RequestId) : IpcMessage;

public abstract record IpcResponse(Guid RequestId) : IpcMessage;

public sealed record GetStatusRequest(Guid RequestId) : IpcRequest(RequestId);

public sealed record StatusResponse(Guid RequestId, ServiceStatus Status) : IpcResponse(RequestId);

public sealed record GetSettingsRequest(Guid RequestId) : IpcRequest(RequestId);

public sealed record SettingsResponse(Guid RequestId, SettingsSnapshot Settings) : IpcResponse(RequestId);

/// <summary>Requires an elevated administrator.</summary>
public sealed record SaveSettingsRequest(Guid RequestId, SettingsUpdate Settings) : IpcRequest(RequestId);

/// <summary>Requires an elevated administrator, because the test may send stored secrets to the given address.</summary>
public sealed record TestConnectionRequest(Guid RequestId, ConnectionTarget Target, SettingsUpdate Settings) : IpcRequest(RequestId);

public sealed record OperationResponse(Guid RequestId, OperationResult Result) : IpcResponse(RequestId);

/// <param name="AfterSequence">0 for the most recent entries; otherwise the last sequence number already received.</param>
public sealed record GetLogsRequest(Guid RequestId, long AfterSequence) : IpcRequest(RequestId);

public sealed record LogsResponse(Guid RequestId, IReadOnlyList<LogEntry> Entries) : IpcResponse(RequestId);

public enum IpcError
{
    /// <summary>The request needs an elevated administrator.</summary>
    Unauthorized,

    /// <summary>The server has no handler for the request, e.g. the control API is not available.</summary>
    NotSupported,

    Failed,
}

public sealed record ErrorResponse(Guid RequestId, IpcError Error, string Message) : IpcResponse(RequestId);
