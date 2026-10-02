using System.Buffers;
using System.Collections.Frozen;
using System.Text.RegularExpressions;
using HADA.Core.Entities;
using HADA.Ipc;
using HADA.Platform.Windows.Actions;
using HADA.Platform.Windows.Sensors;

namespace HADA.Service.CustomSensors;

/// <summary>Ids of built-in entities whose classes live in projects the service does not reference.</summary>
public static class ReservedIds
{
    /// <summary>The tray app's notification entity.</summary>
    public const string Notification = "notification";

    /// <summary>The tray app's media playback sensor.</summary>
    public const string MediaPlayback = "media_playback";

    public const string UpdateAvailable = "update_available";
}

/// <summary>What a custom sensor or button definition must look like, and how it maps to a Home Assistant entity.</summary>
public static partial class CustomSensorRules
{
    public const int MaxSensors = 64;

    private const int MaxIdLength = 64;
    private const int MaxNameLength = 100;
    private const int MaxTextLength = 255;
    private const int MaxProcessNameLength = 260;
    private const int MaxCommandLength = 4096;
    private const int MaxUnitLength = 32;
    private const int MinDeviceIdLength = 8;

    private static readonly SearchValues<char> PathCharacters = SearchValues.Create("\\/:*?\"<>|");

    /// <summary>Ids of the built-in entities, including those the tray registers later and those absent on this computer.</summary>
    public static FrozenSet<string> BuiltInIds { get; } = new[]
    {
        CpuLoadSensor.EntityId,
        MemoryUsageSensor.EntityId,
        BatterySensor.LevelEntityId,
        BatterySensor.ChargingEntityId,
        BatterySensor.PluggedInEntityId,
        PowerStateSensor.DisplayEntityId,
        PowerStateSensor.LidEntityId,
        SessionLockSensor.EntityId,
        LastBootSensor.EntityId,
        LockScreenAction.EntityId,
        ActiveWindowSensor.EntityId,
        AudioVolumeSensor.EntityId,
        UserActivitySensor.EntityId,
        MediaCaptureSensor.MicrophoneEntityId,
        MediaCaptureSensor.CameraEntityId,
        MicrophoneMuteSensor.EntityId,
        ExternalDisplaySensor.EntityId,
        GpuLoadSensor.EntityId,
        NetworkSensor.AddressEntityId,
        NetworkSensor.WifiEntityId,
        ActiveUserSensor.EntityId,
        AudioDeviceSensor.EntityId,
        DoNotDisturbSensor.EntityId,
        PowerActions.SleepEntityId,
        PowerActions.HibernateEntityId,
        PowerActions.ShutdownEntityId,
        PowerActions.RestartEntityId,
        DisplayActions.TurnOffEntityId,
        DisplayActions.WakeEntityId,
        MediaKeyActions.PlayPauseEntityId,
        MediaKeyActions.NextEntityId,
        MediaKeyActions.PreviousEntityId,
        MediaKeyActions.StopEntityId,
        AudioControl.VolumeEntityId,
        AudioControl.MuteEntityId,
        AudioControl.MicrophoneMuteEntityId,
        ReservedIds.Notification,
        ReservedIds.MediaPlayback,
        ReservedIds.UpdateAvailable,
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Whether an id belongs to a built-in entity, including the per-drive ones that have no fixed list.</summary>
    public static bool IsBuiltIn(string id) => BuiltInIds.Contains(id) || DiskUsageSensor.IsEntityId(id);

    /// <summary>Checks one normalized definition. Returns an error message, or <see langword="null"/> when it is valid.</summary>
    public static string? Validate(CustomSensorDefinition sensor)
    {
        var label = sensor.Name.Length > 0 ? sensor.Name : sensor.Id;

        if (sensor.Name.Length is 0 or > MaxNameLength)
        {
            return $"A custom sensor needs a name of at most {MaxNameLength} characters.";
        }

        if (sensor.Id.Length > MaxIdLength || !IdPattern().IsMatch(sensor.Id))
        {
            return $"The ID of custom sensor '{label}' must contain only lowercase letters, digits and underscores.";
        }

        if (IsBuiltIn(sensor.Id))
        {
            return $"The ID '{sensor.Id}' of custom sensor '{label}' is already used by a built-in entity.";
        }

        if (!Enum.IsDefined(sensor.Type))
        {
            return $"Custom sensor '{label}' has an unknown type.";
        }

        if (sensor.Type == CustomSensorType.QuickAction)
        {
            // A shortcut is optional; the tray menu is always there. One without Ctrl, Alt or Win would take a plain
            // key away from every other program.
            return sensor.Value.Length == 0
                || (KeyCombination.TryParse(sensor.Value, out var shortcut)
                    && shortcut.VirtualKey != 0
                    && (shortcut.Modifiers & (KeyModifiers.Ctrl | KeyModifiers.Alt | KeyModifiers.Win)) != 0)
                ? null
                : $"The shortcut of quick action '{label}' must be a key with Ctrl, Alt or Win, such as Ctrl+Alt+L.";
        }

        if (sensor.Value.Length == 0)
        {
            return sensor.Type switch
            {
                CustomSensorType.ProcessRunning => $"Custom sensor '{label}' needs a process name.",
                CustomSensorType.PowerShell => $"Custom sensor '{label}' needs a PowerShell command.",
                CustomSensorType.DeviceConnected => $"Custom sensor '{label}' needs a device ID, such as VID_0BDA&PID_8153.",
                CustomSensorType.CommandButton => $"Custom button '{label}' needs a PowerShell command.",
                CustomSensorType.LaunchButton => $"Custom button '{label}' needs a program, document or address to open.",
                CustomSensorType.KeysButton => $"Custom button '{label}' needs a key combination, such as Ctrl+Shift+M.",
                _ => $"Custom sensor '{label}' needs a value.",
            };
        }

        // A fragment this short is part of nearly every device id, so the sensor would always be on.
        if (sensor.Type == CustomSensorType.DeviceConnected && sensor.Value.Length < MinDeviceIdLength)
        {
            return $"The device ID of custom sensor '{label}' must be at least {MinDeviceIdLength} characters long, such as VID_0BDA&PID_8153.";
        }

        var maxValueLength = sensor.Type switch
        {
            CustomSensorType.ProcessRunning => MaxProcessNameLength,
            CustomSensorType.PowerShell or CustomSensorType.CommandButton or CustomSensorType.LaunchButton => MaxCommandLength,
            _ => MaxTextLength,
        };
        if (sensor.Value.Length > maxValueLength)
        {
            return $"The value of custom sensor '{label}' must be at most {maxValueLength} characters long.";
        }

        if (sensor.Type == CustomSensorType.ProcessRunning && sensor.Value.AsSpan().ContainsAny(PathCharacters))
        {
            return $"Custom sensor '{label}' needs a process name such as 'chrome', not a path.";
        }

        if (sensor.Type == CustomSensorType.KeysButton && !KeyCombination.TryParse(sensor.Value, out _))
        {
            return $"'{sensor.Value}' of custom button '{label}' is not a key combination. Use keys joined by +, such as Ctrl+Shift+M, F11 or MediaNext.";
        }

        if (sensor.Unit.Length > MaxUnitLength)
        {
            return $"The unit of custom sensor '{label}' must be at most {MaxUnitLength} characters long.";
        }

        if (sensor.Type != CustomSensorType.Text
            && !sensor.IsButton
            && sensor.IntervalSeconds is < CustomSensorDefinition.MinIntervalSeconds or > CustomSensorDefinition.MaxIntervalSeconds)
        {
            return $"The interval of custom sensor '{label}' must be between {CustomSensorDefinition.MinIntervalSeconds} and {CustomSensorDefinition.MaxIntervalSeconds} seconds.";
        }

        return null;
    }

    /// <summary>Checks a whole list of normalized definitions, including that their ids are unique.</summary>
    public static string? Validate(IReadOnlyList<CustomSensorDefinition> sensors)
    {
        if (sensors.Count > MaxSensors)
        {
            return $"At most {MaxSensors} custom sensors are supported.";
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sensor in sensors)
        {
            if (Validate(sensor) is { } error)
            {
                return error;
            }

            if (!ids.Add(sensor.Id))
            {
                return $"Two custom sensors share the ID '{sensor.Id}'.";
            }
        }

        return null;
    }

    public static EntityDescriptor ToEntity(CustomSensorDefinition sensor) => new()
    {
        Id = sensor.Id,
        Name = sensor.Name,
        Kind = sensor.IsTrigger ? EntityKind.Trigger
            : sensor.IsButton ? EntityKind.Button
            : sensor.IsBinary ? EntityKind.BinarySensor
            : EntityKind.Sensor,
        Icon = sensor.Type switch
        {
            CustomSensorType.ProcessRunning => "mdi:application-cog-outline",
            CustomSensorType.PowerShell => "mdi:powershell",
            CustomSensorType.DeviceConnected => "mdi:usb-port",
            CustomSensorType.CommandButton => "mdi:console-line",
            CustomSensorType.LaunchButton => "mdi:rocket-launch",
            CustomSensorType.KeysButton => "mdi:keyboard",
            CustomSensorType.QuickAction => "mdi:gesture-tap-button",
            _ => "mdi:form-textbox",
        },

        // Shown in Home Assistant as Connected / Disconnected.
        DeviceClass = sensor.Type == CustomSensorType.DeviceConnected ? "connectivity" : null,
        UnitOfMeasurement = sensor.Unit.Length > 0 ? sensor.Unit : null,

        // A unit tells Home Assistant the value is a number; as a measurement it also gets a history graph.
        StateClass = sensor.Unit.Length > 0 ? "measurement" : null,
    };

    [GeneratedRegex("^[a-z0-9_]+$")]
    private static partial Regex IdPattern();
}
