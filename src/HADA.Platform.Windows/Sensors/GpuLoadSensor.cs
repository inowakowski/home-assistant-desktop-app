using System.Globalization;
using System.Runtime.InteropServices;
using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Hosting;
using HADA.Core.Messaging;
using HADA.Platform.Windows.Interop;
using Microsoft.Extensions.Hosting;

namespace HADA.Platform.Windows.Sensors;

/// <summary>
/// Reads how busy the graphics processor is from Windows' own performance counters, the numbers Task Manager
/// shows. They work with any graphics driver and need neither a kernel driver nor a vendor's tools.
/// </summary>
public sealed class GpuLoad : IDisposable
{
    private const string CounterPath = @"\GPU Engine(*)\Utilization Percentage";
    private const uint FormatDouble = 0x00000200;
    private const uint MoreData = 0x800007D2;

    // PDH_FMT_COUNTERVALUE_ITEM_W on 64-bit Windows: a name pointer, a status, padding, then the value.
    private const int ItemSize = 24;
    private const int NameOffset = 0;
    private const int StatusOffset = 8;
    private const int ValueOffset = 16;

    private const string EngineTypeMarker = "_engtype_";

    private nint _query;
    private nint _counter;

    private GpuLoad(nint query, nint counter)
    {
        _query = query;
        _counter = counter;
    }

    /// <summary>
    /// Returns <see langword="null"/> on a Windows without these counters: before Windows 10 1709, or without a
    /// graphics driver that reports them.
    /// </summary>
    public static GpuLoad? TryOpen()
    {
        try
        {
            if (NativeMethods.PdhOpenQuery(null, 0, out var query) != 0)
            {
                return null;
            }

            // The first collection only sets the baseline; percentages come from the difference to the next one.
            if (NativeMethods.PdhAddEnglishCounter(query, CounterPath, 0, out var counter) != 0
                || NativeMethods.PdhCollectQueryData(query) != 0)
            {
                _ = NativeMethods.PdhCloseQuery(query);
                return null;
            }

            return new GpuLoad(query, counter);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// The load since the previous call, 0 to 100: per kind of work (3D, video decoding, copying, …) the shares of
    /// all programs are added up, and the busiest kind is the answer, as in Task Manager.
    /// Returns <see langword="null"/> when the counters cannot be read right now.
    /// </summary>
    public unsafe double? TryRead()
    {
        ObjectDisposedException.ThrowIf(_query == 0, this);

        if (NativeMethods.PdhCollectQueryData(_query) != 0)
        {
            return null;
        }

        uint size = 0;
        if (NativeMethods.PdhGetFormattedCounterArray(_counter, FormatDouble, ref size, out _, null) != MoreData || size == 0)
        {
            // No program is using the graphics processor at all, or the counters are gone.
            return size == 0 ? 0 : null;
        }

        var buffer = new byte[size];
        fixed (byte* start = buffer)
        {
            if (NativeMethods.PdhGetFormattedCounterArray(_counter, FormatDouble, ref size, out var count, start) != 0)
            {
                return null;
            }

            var byEngineType = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < count; i++)
            {
                var item = start + (i * ItemSize);
                if (*(uint*)(item + StatusOffset) != 0)
                {
                    continue;
                }

                // Instances are named "pid_1234_luid_0x…_phys_0_eng_0_engtype_3D".
                var name = Marshal.PtrToStringUni(*(nint*)(item + NameOffset)) ?? string.Empty;
                var marker = name.LastIndexOf(EngineTypeMarker, StringComparison.OrdinalIgnoreCase);
                var engineType = marker >= 0 ? name[(marker + EngineTypeMarker.Length)..] : name;
                byEngineType[engineType] = byEngineType.GetValueOrDefault(engineType) + *(double*)(item + ValueOffset);
            }

            return byEngineType.Count == 0 ? 0 : Math.Clamp(byEngineType.Values.Max(), 0, 100);
        }
    }

    public void Dispose()
    {
        if (_query != 0)
        {
            _ = NativeMethods.PdhCloseQuery(_query);
            _query = 0;
            _counter = 0;
        }
    }
}

/// <summary>
/// Publishes the load of the graphics processor. Runs in the service; not registered on a computer whose Windows
/// or graphics driver does not report it.
/// </summary>
public sealed class GpuLoadSensor(IEventBus bus, IEntityRegistry registry) : EagerBackgroundService
{
    public const string EntityId = BuiltInEntityIds.GpuLoad;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var gpu = GpuLoad.TryOpen();
        if (gpu is null)
        {
            return;
        }

        // Registered with the first reading, so a computer whose counters exist but never answer gets no dead entity.
        var registered = false;
        var publisher = new ChangeOnlyPublisher(bus);
        using var timer = new PeriodicTimer(PollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (gpu.TryRead() is not { } load)
            {
                continue;
            }

            if (!registered)
            {
                registered = true;
                await registry.RegisterAsync(
                    new EntityDescriptor
                    {
                        Id = EntityId,
                        Name = "GPU load",
                        Kind = EntityKind.Sensor,
                        Icon = "mdi:expansion-card",
                        UnitOfMeasurement = "%",
                        StateClass = "measurement",
                    },
                    stoppingToken);
            }

            await publisher.PublishAsync(EntityId, Math.Round(load).ToString(CultureInfo.InvariantCulture), cancellationToken: stoppingToken);
        }
    }
}
