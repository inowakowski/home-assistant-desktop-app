using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Messaging;
using HADA.Core.Models;
using Microsoft.Extensions.Logging;

namespace HADA.Core.Actions;

/// <summary>
/// A switch that keeps the computer from going to sleep by itself while it is on, e.g. through a long download or
/// a render. Runs in the service. It is off whenever HADA starts, so no computer stays awake because of a switch
/// nobody remembers turning on.
/// </summary>
public sealed partial class KeepAwakeControl(IEventBus bus, IEntityRegistry registry, ILogger<KeepAwakeControl> logger, IKeepAwake keepAwake)
    : CommandHandler(bus, registry, logger), IDisposable
{
    private readonly ChangeOnlyPublisher _publisher = new(bus);
    private readonly SemaphoreSlim _switching = new(1, 1);
    private bool _isHeld;

    protected override IReadOnlyList<EntityDescriptor> Entities { get; } =
    [
        new() { Id = BuiltInEntityIds.KeepAwake, Name = "Keep awake", Kind = EntityKind.Switch, Icon = "mdi:coffee" },
    ];

    public override void Dispose()
    {
        if (_isHeld)
        {
            keepAwake.Release();
            _isHeld = false;
        }

        _switching.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    protected override async ValueTask HandleAsync(ActionCommand command, CancellationToken cancellationToken)
    {
        await _switching.WaitAsync(cancellationToken);
        try
        {
            var on = command.Value == BinaryState.On;
            if (on && !_isHeld)
            {
                if (keepAwake.Hold() is { } reason)
                {
                    LogFailed(Logger, reason);
                }
                else
                {
                    _isHeld = true;
                }
            }
            else if (!on && _isHeld)
            {
                keepAwake.Release();
                _isHeld = false;
            }

            // Home Assistant shows what is, also when holding sleep off did not work.
            await _publisher.PublishAsync(BuiltInEntityIds.KeepAwake, BinaryState.From(_isHeld), cancellationToken: cancellationToken);
        }
        finally
        {
            _switching.Release();
        }
    }

    protected override async Task RunAsync(CancellationToken cancellationToken)
    {
        await _switching.WaitAsync(cancellationToken);
        try
        {
            await _publisher.PublishAsync(BuiltInEntityIds.KeepAwake, BinaryState.From(_isHeld), cancellationToken: cancellationToken);
        }
        finally
        {
            _switching.Release();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The computer could not be kept awake: {Reason}")]
    private static partial void LogFailed(ILogger logger, string reason);
}
