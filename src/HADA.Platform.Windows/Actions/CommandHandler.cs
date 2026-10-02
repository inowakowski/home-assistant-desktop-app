using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HADA.Platform.Windows.Actions;

/// <summary>
/// Registers entities Home Assistant can send commands to, and hands every command for one of them to
/// <see cref="HandleAsync"/>. A command that fails is logged; the next one is still handled.
/// </summary>
public abstract partial class CommandHandler(IEventBus bus, IEntityRegistry registry, ILogger logger) : BackgroundService
{
    protected IEventBus Bus => bus;

    protected ILogger Logger => logger;

    /// <summary>The entities to register. Read once, when the handler starts.</summary>
    protected abstract IReadOnlyList<EntityDescriptor> Entities { get; }

    /// <param name="command">
    /// A command for one of <see cref="Entities"/>. Engines have already checked its
    /// <see cref="ActionCommand.Value"/> against the entity's kind.
    /// </param>
    protected abstract ValueTask HandleAsync(ActionCommand command, CancellationToken cancellationToken);

    /// <summary>Runs next to the command loop, e.g. to report the state of a switch. Does nothing by default.</summary>
    protected virtual Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Subscribe before registering so a command arriving right after discovery is not missed.
        await using var commands = bus.Subscribe<ActionCommand>();

        var entities = Entities;
        var ids = entities.Select(entity => entity.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var entity in entities)
        {
            await registry.RegisterAsync(entity, stoppingToken);
        }

        var running = Task.Run(() => RunAsync(stoppingToken), CancellationToken.None);
        try
        {
            await foreach (var command in commands.ReadAllAsync(stoppingToken))
            {
                if (!ids.Contains(command.ActionId))
                {
                    continue;
                }

                try
                {
                    await HandleAsync(command, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogCommandFailed(logger, ex, command.ActionId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await running;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The command for '{EntityId}' failed.")]
    private static partial void LogCommandFailed(ILogger logger, Exception exception, string entityId);
}
