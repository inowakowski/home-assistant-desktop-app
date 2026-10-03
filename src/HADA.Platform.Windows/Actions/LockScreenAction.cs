using System.Diagnostics;
using System.Runtime.InteropServices;
using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Hosting;
using HADA.Core.Models;
using HADA.Platform.Windows.Interop;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HADA.Platform.Windows.Actions;

/// <summary>Exposes a "Lock screen" button and locks the interactive session when it is pressed.</summary>
public sealed partial class LockScreenAction(IEventBus bus, IEntityRegistry registry, ILogger<LockScreenAction> logger)
    : EagerBackgroundService
{
    public const string EntityId = BuiltInEntityIds.LockScreen;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Subscribe before registering so a press arriving right after discovery is not missed.
        await using var commands = bus.Subscribe<ActionCommand>();

        await registry.RegisterAsync(
            new EntityDescriptor
            {
                Id = EntityId,
                Name = "Lock screen",
                Kind = EntityKind.Button,
                Icon = "mdi:monitor-lock",
            },
            stoppingToken);

        await foreach (var command in commands.ReadAllAsync(stoppingToken))
        {
            if (command.ActionId != EntityId)
            {
                continue;
            }

            if (TryLock(out var error))
            {
                LogLocked(logger, command.Origin);
            }
            else
            {
                LogLockFailed(logger, error);
            }
        }
    }

    /// <remarks>
    /// <c>LockWorkStation</c> only works from inside the interactive session. A Windows service runs in session 0,
    /// so there the active console session is disconnected instead, which returns it to the lock screen.
    /// </remarks>
    private static bool TryLock(out int error)
    {
        using var process = Process.GetCurrentProcess();

        bool locked;
        if (process.SessionId != 0)
        {
            locked = NativeMethods.LockWorkStation();
        }
        else
        {
            var session = NativeMethods.WTSGetActiveConsoleSessionId();
            locked = session != NativeMethods.NoActiveConsoleSession
                && NativeMethods.WTSDisconnectSession(server: 0, session, wait: false);
        }

        error = locked ? 0 : Marshal.GetLastPInvokeError();
        return locked;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Screen locked (requested via {Origin}).")]
    private static partial void LogLocked(ILogger logger, string? origin);

    [LoggerMessage(Level = LogLevel.Error, Message = "Locking the screen failed with error {Error}.")]
    private static partial void LogLockFailed(ILogger logger, int error);
}
