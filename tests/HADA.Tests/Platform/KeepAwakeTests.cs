using System.Diagnostics;
using System.Runtime.Versioning;
using HADA.Core.Abstractions;
using HADA.Core.Actions;
using HADA.Core.Entities;
using HADA.Core.Messaging;
using HADA.Core.Models;
using HADA.Core.Unix;
using HADA.Platform.Linux;
using HADA.Platform.MacOS;
using HADA.Platform.Windows.Actions;
using Microsoft.Extensions.Logging.Abstractions;

namespace HADA.Tests.Platform;

/// <summary>The "Keep awake" switch, and how each system holds sleep off.</summary>
public sealed class KeepAwakeTests : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly ChannelEventBus _bus = new();
    private readonly EntityRegistry _registry;

    public KeepAwakeTests() => _registry = new EntityRegistry(_bus);

    public ValueTask DisposeAsync() => _bus.DisposeAsync();

    [Fact]
    public async Task The_switch_starts_off_holds_sleep_off_and_lets_go()
    {
        var keepAwake = new FakeKeepAwake();
        await using var readings = _bus.Subscribe<TelemetryEvent>();
        using var control = new KeepAwakeControl(_bus, _registry, NullLogger<KeepAwakeControl>.Instance, keepAwake);
        await control.StartAsync(CancellationToken.None);

        Assert.True(_registry.TryGet(BuiltInEntityIds.KeepAwake, out var entity));
        Assert.Equal(EntityKind.Switch, entity.Kind);
        await WaitForStateAsync(readings, BinaryState.Off);

        await _bus.PublishAsync(new ActionCommand { ActionId = BuiltInEntityIds.KeepAwake, Value = BinaryState.On });
        await WaitForStateAsync(readings, BinaryState.On);
        await _bus.PublishAsync(new ActionCommand { ActionId = BuiltInEntityIds.KeepAwake, Value = BinaryState.Off });
        await WaitForStateAsync(readings, BinaryState.Off);

        Assert.Equal(["hold", "release"], keepAwake.Calls);
        await control.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_switch_the_system_refused_stays_off_and_one_left_on_is_let_go_when_hada_stops()
    {
        var keepAwake = new FakeKeepAwake { Refusal = "no logind here" };
        await using var readings = _bus.Subscribe<TelemetryEvent>();
        var control = new KeepAwakeControl(_bus, _registry, NullLogger<KeepAwakeControl>.Instance, keepAwake);
        await control.StartAsync(CancellationToken.None);
        await WaitForStateAsync(readings, BinaryState.Off);

        await _bus.PublishAsync(new ActionCommand { ActionId = BuiltInEntityIds.KeepAwake, Value = BinaryState.On });
        await WaitUntilAsync(() => keepAwake.Calls.Count == 1);
        keepAwake.Refusal = null;
        await _bus.PublishAsync(new ActionCommand { ActionId = BuiltInEntityIds.KeepAwake, Value = BinaryState.On });
        await WaitForStateAsync(readings, BinaryState.On);

        await control.StopAsync(CancellationToken.None);
        control.Dispose();
        Assert.Equal(["hold", "hold", "release"], keepAwake.Calls);
    }

    [Fact]
    public void The_tools_are_told_to_end_with_hada()
    {
        Assert.Equal(["-i", "-w", "4242"], MacKeepAwake.Arguments(4242));

        var linux = LinuxKeepAwake.Arguments(4242);
        Assert.Contains("--what=sleep:idle", linux);
        Assert.Contains("--mode=block", linux);
        Assert.Contains("--pid=4242", linux);
    }

    [Fact]
    public void A_tool_that_ends_at_once_says_why_and_one_that_is_missing_too()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var refusing = new KeepAwakeProcess("false", _ => []);
        Assert.Contains("exited with code 1", refusing.Hold(), StringComparison.Ordinal);

        using var missing = new KeepAwakeProcess("no-such-tool-for-hada", _ => []);
        Assert.Contains("missing", missing.Hold(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_tool_that_holds_runs_until_let_go()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var holding = new KeepAwakeProcess("sleep", _ => ["60"]);
        Assert.Null(holding.Hold());
        Assert.Null(holding.Hold());
        var sleeper = Assert.Single(Process.GetProcessesByName("sleep"), process => IsChildOfThisProcess(process));

        holding.Release();

        Assert.True(sleeper.WaitForExit(Timeout));
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void Windows_takes_and_drops_a_power_request()
    {
        using var keepAwake = new WindowsKeepAwake();

        Assert.Null(keepAwake.Hold());
        Assert.Null(keepAwake.Hold());
        keepAwake.Release();
        Assert.Null(keepAwake.Hold());
    }

    [MacOSFact]
    [SupportedOSPlatform("macos")]
    public void Caffeinate_holds_the_mac_awake()
    {
        using var keepAwake = MacKeepAwake.Create();

        Assert.Null(keepAwake.Hold());
        Assert.Contains("caffeinate", Tools.Run("pmset", "-g", "assertions")?.Output ?? string.Empty, StringComparison.Ordinal);
        keepAwake.Release();
    }

    // Read from /proc on Linux; elsewhere, any sleep started just now is ours, as no other test starts one.
    private static bool IsChildOfThisProcess(Process process) =>
        !OperatingSystem.IsLinux()
        || (File.ReadAllText($"/proc/{process.Id}/stat").Split(' ') is var fields && fields.Length > 3 && fields[3] == Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static async Task WaitForStateAsync(IEventSubscription<TelemetryEvent> readings, string state)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        await foreach (var reading in readings.ReadAllAsync(timeout.Token))
        {
            if (reading.SensorId == BuiltInEntityIds.KeepAwake && reading.State == state)
            {
                return;
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        while (!condition())
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    private sealed class FakeKeepAwake : IKeepAwake
    {
        public List<string> Calls { get; } = [];

        public string? Refusal { get; set; }

        public string? Hold()
        {
            lock (Calls)
            {
                Calls.Add("hold");
            }

            return Refusal;
        }

        public void Release()
        {
            lock (Calls)
            {
                Calls.Add("release");
            }
        }
    }
}
