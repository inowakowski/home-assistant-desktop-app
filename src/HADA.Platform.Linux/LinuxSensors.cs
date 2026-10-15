using System.Globalization;
using System.Runtime.Versioning;
using HADA.Core.Abstractions;
using HADA.Core.Actions;
using HADA.Core.Entities;
using HADA.Core.Hosting;
using HADA.Core.Messaging;
using HADA.Core.Models;
using Microsoft.Extensions.Logging;

namespace HADA.Platform.Linux;

/// <summary>Publishes whether the laptop's lid is open. Registers nothing where there is no lid.</summary>
public sealed class LidSensor(IEventBus bus, IEntityRegistry registry, LinuxHardware hardware) : EagerBackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (hardware.ReadLidOpen() is null)
        {
            return;
        }

        await registry.RegisterAsync(BuiltInDescriptors.Lid, stoppingToken);
        var publisher = new ChangeOnlyPublisher(bus);
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            if (hardware.ReadLidOpen() is { } open)
            {
                await publisher.PublishAsync(BuiltInEntityIds.LidOpen, BinaryState.From(open), cancellationToken: stoppingToken);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

/// <summary>
/// Publishes how busy the graphics card is, where Linux tells: AMD's cards through the kernel, NVIDIA's through
/// their driver's <c>nvidia-smi</c>. Registered with the first reading; Intel's cards keep it to themselves.
/// </summary>
public sealed class GpuLoadSensor(IEventBus bus, IEntityRegistry registry, LinuxHardware hardware, Func<string, string[], ToolResult?>? run = null)
    : EagerBackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    private readonly Func<string, string[], ToolResult?> _run = run ?? Tools.Run;

    /// <summary>Reads <c>nvidia-smi --query-gpu=utilization.gpu --format=csv,noheader,nounits</c>: a number per card; the busiest counts.</summary>
    public static int? ParseNvidiaSmi(string output)
    {
        var loads = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(line => int.TryParse(line, NumberStyles.None, CultureInfo.InvariantCulture, out var load) ? load : (int?)null)
            .OfType<int>()
            .ToList();
        return loads.Count > 0 ? Math.Clamp(loads.Max(), 0, 100) : null;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Without nvidia-smi now, it is not there later either; asking again every ten seconds would be wasted.
        var hasNvidia = Tools.Find("nvidia-smi") is not null;
        var registered = false;
        var publisher = new ChangeOnlyPublisher(bus);
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            var load = hardware.ReadGpuBusyPercent() ?? (hasNvidia ? ReadNvidia() : null);
            if (load is null)
            {
                if (!registered && !hasNvidia)
                {
                    return;
                }

                continue;
            }

            if (!registered)
            {
                registered = true;
                await registry.RegisterAsync(BuiltInDescriptors.GpuLoad, stoppingToken);
            }

            await publisher.PublishAsync(BuiltInEntityIds.GpuLoad, load.Value.ToString(CultureInfo.InvariantCulture), cancellationToken: stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private int? ReadNvidia() =>
        _run("nvidia-smi", ["--query-gpu=utilization.gpu", "--format=csv,noheader,nounits"]) is { Succeeded: true } result
            ? ParseNvidiaSmi(result.Output)
            : null;
}

/// <summary>Publishes whether a screen other than the laptop's own is connected, with how many there are.</summary>
public sealed class ExternalDisplaySensor(IEventBus bus, IEntityRegistry registry, LinuxHardware hardware) : EagerBackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (hardware.ReadDisplays() is null)
        {
            return;
        }

        await registry.RegisterAsync(BuiltInDescriptors.ExternalDisplay, stoppingToken);
        var publisher = new ChangeOnlyPublisher(bus);
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            if (hardware.ReadDisplays() is { } displays)
            {
                await publisher.PublishAsync(
                    BuiltInEntityIds.ExternalDisplay,
                    BinaryState.From(displays.External > 0),
                    new Dictionary<string, object?> { ["displays"] = displays.Total, ["external_displays"] = displays.External },
                    stoppingToken);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

/// <summary>
/// Publishes whether the desktop keeps notifications quiet: KDE's do-not-disturb, which its notification server
/// reports as inhibited, or GNOME's, which is notification banners switched off.
/// </summary>
public sealed class DoNotDisturbSensor(IEventBus bus, IEntityRegistry registry, Func<string, string[], ToolResult?>? run = null) : EagerBackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private static readonly (string Tool, string[] Arguments, Func<string, bool?> Parse)[] Sources =
    [
        ("busctl", ["--user", "get-property", "org.freedesktop.Notifications", "/org/freedesktop/Notifications", "org.freedesktop.Notifications", "Inhibited"], ParseBusctlBool),
        ("gsettings", ["get", "org.gnome.desktop.notifications", "show-banners"], output => ParseGsettingsBool(output) is { } banners ? !banners : null),
    ];

    private readonly Func<string, string[], ToolResult?> _run = run ?? Tools.Run;
    private (string Tool, string[] Arguments, Func<string, bool?> Parse)? _source;

    /// <summary>Reads <c>busctl get-property</c>'s answer for a boolean: <c>b true</c>.</summary>
    public static bool? ParseBusctlBool(string output) => output.Trim() switch
    {
        "b true" => true,
        "b false" => false,
        _ => null,
    };

    /// <summary>Reads <c>gsettings get</c>'s answer for a boolean: <c>true</c>.</summary>
    public static bool? ParseGsettingsBool(string output) => output.Trim() switch
    {
        "true" => true,
        "false" => false,
        _ => null,
    };

    /// <summary>Whether notifications are kept quiet; <see langword="null"/> where the desktop does not say.</summary>
    public bool? Read()
    {
        if (_source is { } known)
        {
            return Ask(known);
        }

        foreach (var source in Sources)
        {
            if (Ask(source) is { } quiet)
            {
                _source = source;
                return quiet;
            }
        }

        return null;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Read() is null)
        {
            return;
        }

        await registry.RegisterAsync(BuiltInDescriptors.DoNotDisturb, stoppingToken);
        var publisher = new ChangeOnlyPublisher(bus);
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            if (Read() is { } quiet)
            {
                // Linux has one way of being quiet; Windows' kinds of it are "priority_only" and "alarms_only".
                await publisher.PublishAsync(
                    BuiltInEntityIds.DoNotDisturb,
                    BinaryState.From(quiet),
                    new Dictionary<string, object?> { ["mode"] = quiet ? "on" : "off" },
                    stoppingToken);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private bool? Ask((string Tool, string[] Arguments, Func<string, bool?> Parse) source) =>
        _run(source.Tool, source.Arguments) is { Succeeded: true } result ? source.Parse(result.Output) : null;
}

/// <summary>Publishes the window in front, in an X11 session. Registers nothing on Wayland, which keeps it to the desktop.</summary>
[SupportedOSPlatform("linux")]
public sealed class ActiveWindowSensor(IEventBus bus, IEntityRegistry registry) : EagerBackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var windows = X11ActiveWindow.Open();
        if (windows is null)
        {
            return;
        }

        await registry.RegisterAsync(BuiltInDescriptors.ActiveWindow, stoppingToken);
        ActiveWindowInfo? last = null;
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            if (windows.Read() is not { } window || window == last)
            {
                continue;
            }

            last = window;
            await bus.PublishAsync(
                new TelemetryEvent
                {
                    SensorId = BuiltInEntityIds.ActiveWindow,
                    State = window.Title.Length > 0 ? window.Title : window.ProcessName ?? "unknown",
                    Attributes = new Dictionary<string, object?> { ["process_name"] = window.ProcessName },
                },
                stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

/// <summary>
/// Lets Home Assistant set the screen's brightness, and reports it. Read from the kernel's backlight; set through
/// systemd-logind, which lets the user at the computer do it without a password. Registers nothing without a
/// backlight, as on a desktop.
/// </summary>
public sealed partial class ScreenBrightnessControl(
    IEventBus bus, IEntityRegistry registry, ILogger<ScreenBrightnessControl> logger, LinuxHardware hardware, Func<string, string[], ToolResult?>? run = null)
    : CommandHandler(bus, registry, logger), IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly Func<string, string[], ToolResult?> _run = run ?? Tools.Run;
    private readonly ChangeOnlyPublisher _publisher = new(bus);
    private readonly SemaphoreSlim _reporting = new(1, 1);

    protected override IReadOnlyList<EntityDescriptor> Entities { get; } = hardware.ReadBacklight() is null ? [] : [BuiltInDescriptors.ScreenBrightness];

    /// <summary>The level to set for <paramref name="percent"/> of the backlight's range; the lowest step above off keeps the screen readable.</summary>
    public static int LevelFor(Backlight backlight, double percent) =>
        Math.Clamp((int)Math.Round(backlight.MaxLevel * Math.Clamp(percent, 0, 100) / 100), 1, backlight.MaxLevel);

    public override void Dispose()
    {
        _reporting.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    protected override async ValueTask HandleAsync(ActionCommand command, CancellationToken cancellationToken)
    {
        if (!double.TryParse(command.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)
            || hardware.ReadBacklight() is not { } backlight)
        {
            return;
        }

        var level = LevelFor(backlight, percent);
        var result = _run(
            "busctl",
            ["call", "org.freedesktop.login1", "/org/freedesktop/login1/session/auto", "org.freedesktop.login1.Session", "SetBrightness",
                "ssu", "backlight", backlight.Name, level.ToString(CultureInfo.InvariantCulture)]);
        if (result is not { Succeeded: true })
        {
            LogFailed(Logger, Tools.Describe("busctl", result));
        }

        // Home Assistant shows the new state at once, instead of the old one until the next poll.
        await ReportAsync(cancellationToken);
    }

    protected override async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Entities.Count == 0)
        {
            return;
        }

        try
        {
            using var timer = new PeriodicTimer(PollInterval);
            do
            {
                await ReportAsync(cancellationToken);
            }
            while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task ReportAsync(CancellationToken cancellationToken)
    {
        await _reporting.WaitAsync(cancellationToken);
        try
        {
            if (hardware.ReadBacklight() is { } backlight)
            {
                await _publisher.PublishAsync(
                    BuiltInEntityIds.ScreenBrightness, backlight.Percent.ToString(CultureInfo.InvariantCulture), cancellationToken: cancellationToken);
            }
        }
        finally
        {
            _reporting.Release();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The screen's brightness could not be set: {Reason}")]
    private static partial void LogFailed(ILogger logger, string reason);
}
