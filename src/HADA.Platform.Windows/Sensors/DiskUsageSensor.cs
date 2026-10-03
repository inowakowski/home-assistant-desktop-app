using System.Globalization;
using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Hosting;
using HADA.Core.Messaging;
using Microsoft.Extensions.Hosting;

namespace HADA.Platform.Windows.Sensors;

/// <param name="Letter">Lowercase drive letter.</param>
public readonly record struct DiskUsage(char Letter, int UsedPercent, double FreeGigabytes, double TotalGigabytes);

public static class Disks
{
    private const double Gigabyte = 1024d * 1024 * 1024;

    /// <summary>The built-in drives that are ready, i.e. not USB sticks, network drives or empty card readers.</summary>
    public static IReadOnlyList<DiskUsage> Read()
    {
        var disks = new List<DiskUsage>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                var letter = char.ToLowerInvariant(drive.Name[0]);
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady || drive.TotalSize <= 0 || letter is < 'a' or > 'z')
                {
                    continue;
                }

                var used = drive.TotalSize - drive.TotalFreeSpace;
                disks.Add(new DiskUsage(
                    letter,
                    (int)Math.Round(100d * used / drive.TotalSize),
                    Math.Round(drive.TotalFreeSpace / Gigabyte, 1),
                    Math.Round(drive.TotalSize / Gigabyte, 1)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A drive that went away between being listed and being read.
            }
        }

        return disks;
    }
}

/// <summary>
/// Publishes how full each built-in drive is, as <c>disk_c_usage</c>, <c>disk_d_usage</c> and so on, with the
/// free and total space as attributes. Runs in the service.
/// </summary>
public sealed class DiskUsageSensor(IEventBus bus, IEntityRegistry registry) : EagerBackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    public static string EntityIdFor(char letter) => BuiltInEntityIds.DiskUsage(letter);

    /// <summary>Whether an id is one this sensor may register, so a custom sensor cannot take it.</summary>
    public static bool IsEntityId(string id) => BuiltInEntityIds.IsDiskUsage(id);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var registered = new HashSet<char>();
        var publisher = new ChangeOnlyPublisher(bus);
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            foreach (var disk in Disks.Read())
            {
                var id = EntityIdFor(disk.Letter);

                // Registered when first seen, so a drive added later, e.g. a BitLocker drive unlocked after sign-in, appears too.
                if (registered.Add(disk.Letter))
                {
                    await registry.RegisterAsync(
                        new EntityDescriptor
                        {
                            Id = id,
                            Name = $"Disk {char.ToUpperInvariant(disk.Letter)} usage",
                            Kind = EntityKind.Sensor,
                            Icon = "mdi:harddisk",
                            UnitOfMeasurement = "%",
                            StateClass = "measurement",
                        },
                        stoppingToken);
                }

                await publisher.PublishAsync(
                    id,
                    disk.UsedPercent.ToString(CultureInfo.InvariantCulture),
                    new Dictionary<string, object?> { ["free_gb"] = disk.FreeGigabytes, ["total_gb"] = disk.TotalGigabytes },
                    stoppingToken);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
