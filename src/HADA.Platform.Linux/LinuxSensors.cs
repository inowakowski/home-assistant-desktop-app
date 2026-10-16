using System.Globalization;
using System.Runtime.Versioning;
using HADA.Core.Abstractions;
using HADA.Core.Actions;
using HADA.Core.Entities;
using HADA.Core.Hosting;
using HADA.Core.Messaging;
using HADA.Core.Models;
using HADA.Core.Unix;
using Microsoft.Extensions.Logging;

namespace HADA.Platform.Linux;

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
