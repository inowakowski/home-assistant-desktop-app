using System.Text.Json.Serialization;
using HADA.Core.Entities;
using HADA.Core.Models;

namespace HADA.Ipc;

[JsonPolymorphic]
[JsonDerivedType(typeof(HelloMessage), "hello")]
[JsonDerivedType(typeof(EntityRegistrationMessage), "entity")]
[JsonDerivedType(typeof(TelemetryMessage), "telemetry")]
public abstract record IpcMessage;

/// <summary>First message from a client. The server drops clients speaking another protocol version.</summary>
public sealed record HelloMessage(int ProtocolVersion, string ClientName) : IpcMessage;

public sealed record EntityRegistrationMessage(EntityDescriptor Entity) : IpcMessage;

public sealed record TelemetryMessage(TelemetryEvent Reading) : IpcMessage;
