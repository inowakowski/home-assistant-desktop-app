namespace HADA.Core.Models;

/// <summary>Outcome of trying engine settings against the real server, with a message for the user.</summary>
public sealed record ConnectionTestResult(bool Success, string Message);
