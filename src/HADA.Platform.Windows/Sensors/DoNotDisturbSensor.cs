using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Messaging;
using HADA.Platform.Windows.Interop;
using Microsoft.Extensions.Hosting;

namespace HADA.Platform.Windows.Sensors;

public enum QuietMode
{
    Off = 0,

    /// <summary>"Do not disturb" on Windows 11, "Priority only" on Windows 10.</summary>
    PriorityOnly = 1,

    AlarmsOnly = 2,
}

public static class QuietHours
{
    // WNF_SHEL_QUIETHOURS_ACTIVE_PROFILE_CHANGED. Windows has no documented way to ask for this.
    private const ulong ActiveProfile = 0x0D83063EA3BF1C75;

    /// <summary>
    /// Whether Windows is holding notifications back for the calling user. Returns <see langword="null"/> when this
    /// version of Windows does not tell.
    /// </summary>
    public static unsafe QuietMode? TryRead()
    {
        var buffer = stackalloc byte[sizeof(int)];
        var size = (uint)sizeof(int);
        try
        {
            if (NativeMethods.NtQueryWnfStateData(ActiveProfile, 0, 0, out _, buffer, ref size) != 0 || size != sizeof(int))
            {
                return null;
            }
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }

        var mode = *(int*)buffer;
        return Enum.IsDefined((QuietMode)mode) ? (QuietMode)mode : null;
    }
}

/// <summary>
/// Publishes whether "Do not disturb" (Focus assist on Windows 10) is on. Runs in the tray app, because the
/// setting belongs to the user. Not registered at all on a Windows that does not tell.
/// </summary>
public sealed class DoNotDisturbSensor(IEventBus bus, IEntityRegistry registry) : BackgroundService
{
    public const string EntityId = "do_not_disturb";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Registered when Windows first tells: right after sign-in the shell has not published the state yet.
        var registered = false;
        var publisher = new ChangeOnlyPublisher(bus);
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            if (QuietHours.TryRead() is not { } mode)
            {
                continue;
            }

            if (!registered)
            {
                registered = true;
                await registry.RegisterAsync(
                    new EntityDescriptor { Id = EntityId, Name = "Do not disturb", Kind = EntityKind.BinarySensor, Icon = "mdi:bell-off" },
                    stoppingToken);
            }

            await publisher.PublishAsync(
                EntityId,
                BinaryState.From(mode != QuietMode.Off),
                new Dictionary<string, object?>
                {
                    ["mode"] = mode switch
                    {
                        QuietMode.PriorityOnly => "priority_only",
                        QuietMode.AlarmsOnly => "alarms_only",
                        _ => "off",
                    },
                },
                stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
