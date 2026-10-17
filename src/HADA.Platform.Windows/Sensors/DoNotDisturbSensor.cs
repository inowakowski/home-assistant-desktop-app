using System.Runtime.InteropServices;
using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Hosting;
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
    /// <summary>The profile of "Do not disturb" off.</summary>
    public const string UnrestrictedProfile = "Microsoft.QuietHoursProfile.Unrestricted";

    /// <summary>The profile of "Do not disturb" on Windows 11, "Priority only" on Windows 10.</summary>
    public const string PriorityOnlyProfile = "Microsoft.QuietHoursProfile.PriorityOnly";

    /// <summary>
    /// The profile the user chose, as the Settings app's switch sets it, e.g. <see cref="UnrestrictedProfile"/>;
    /// <see langword="null"/> where Windows does not tell.
    /// </summary>
    public static string? TryReadProfile() => With(settings =>
        settings.GetUserSelectedProfile(out var profile) == 0 && !string.IsNullOrEmpty(profile) ? profile : null);

    /// <summary>Turns "Do not disturb" on or off, as its switch in Settings does. Returns <see langword="null"/> when done, otherwise why not.</summary>
    public static string? TrySet(bool on) => TrySetProfile(on ? PriorityOnlyProfile : UnrestrictedProfile);

    /// <summary>Chooses a profile, as the Settings app does. Returns <see langword="null"/> when done, otherwise why not.</summary>
    public static string? TrySetProfile(string profile)
    {
        ArgumentException.ThrowIfNullOrEmpty(profile);
        IQuietHoursSettings? settings = null;
        try
        {
            settings = (IQuietHoursSettings)new QuietHoursSettingsComObject();
            var result = settings.SetUserSelectedProfile(profile);
            return result == 0 ? null : $"Windows refused it with error 0x{result:X8}.";
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return $"Windows' quiet hours settings cannot be reached: {ex.Message}";
        }
        finally
        {
            if (settings is not null)
            {
                Marshal.ReleaseComObject(settings);
            }
        }
    }

    private static T? With<T>(Func<IQuietHoursSettings, T?> use)
        where T : class
    {
        IQuietHoursSettings? settings = null;
        try
        {
            settings = (IQuietHoursSettings)new QuietHoursSettingsComObject();
            return use(settings);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
        finally
        {
            if (settings is not null)
            {
                Marshal.ReleaseComObject(settings);
            }
        }
    }
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
public sealed class DoNotDisturbSensor(IEventBus bus, IEntityRegistry registry) : EagerBackgroundService
{
    public const string EntityId = BuiltInEntityIds.DoNotDisturb;

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
                    BuiltInDescriptors.DoNotDisturb,
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

/// <summary>
/// "Do not disturb" through the shell's quiet hours: read as the sensor reads it, switched as the Settings app's
/// switch does it. Off is the unrestricted profile; on, "Do not disturb" on Windows 11 and "Priority only" on 10.
/// </summary>
public sealed class WindowsDoNotDisturb : IDoNotDisturb
{
    public bool? Read() => QuietHours.TryRead() is { } mode ? mode != QuietMode.Off : null;

    public bool CanSet => QuietHours.TryReadProfile() is not null;

    public Task<string?> SetAsync(bool on, CancellationToken cancellationToken) => Task.FromResult(QuietHours.TrySet(on));
}
