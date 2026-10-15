using System.Globalization;
using HADA.Core.Abstractions;

namespace HADA.Platform.Linux;

/// <summary>
/// What the kernel says in <c>/proc</c>. Each reading is a parser of the file's text, so it can be checked with
/// any text, and a read of the file itself, which gives <see langword="null"/> where there is no such file.
/// </summary>
public static class Proc
{
    public static CpuSample? ReadCpu() => ParseCpu(ReadText("/proc/stat"));

    public static int? ReadMemoryLoadPercent() => ParseMemoryLoadPercent(ReadText("/proc/meminfo"));

    public static DateTimeOffset? ReadBootTime() => ParseBootTime(ReadText("/proc/stat"));

    /// <summary>
    /// The first line of <c>/proc/stat</c>: user, nice, system, idle, iowait, irq, softirq and steal time, in clock
    /// ticks, of all CPUs together. Waiting for the disk is not being busy; guest time is already in user time.
    /// </summary>
    public static CpuSample? ParseCpu(string? stat)
    {
        if (stat is null)
        {
            return null;
        }

        foreach (var line in stat.AsSpan().EnumerateLines())
        {
            if (!line.StartsWith("cpu "))
            {
                continue;
            }

            var fields = line[4..].ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 4)
            {
                return null;
            }

            long total = 0;
            long idle = 0;
            for (var index = 0; index < Math.Min(fields.Length, 8); index++)
            {
                if (!long.TryParse(fields[index], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks))
                {
                    return null;
                }

                total += ticks;
                if (index is 3 or 4)
                {
                    idle += ticks;
                }
            }

            return new CpuSample(total - idle, total);
        }

        return null;
    }

    /// <summary>The memory in use, from <c>MemTotal</c> and <c>MemAvailable</c>, which leaves out what caches would give back.</summary>
    public static int? ParseMemoryLoadPercent(string? meminfo)
    {
        if (meminfo is null)
        {
            return null;
        }

        long? total = null;
        long? available = null;
        foreach (var line in meminfo.AsSpan().EnumerateLines())
        {
            if (line.StartsWith("MemTotal:"))
            {
                total = Kilobytes(line["MemTotal:".Length..]);
            }
            else if (line.StartsWith("MemAvailable:"))
            {
                available = Kilobytes(line["MemAvailable:".Length..]);
            }
        }

        return total is > 0 && available is { } free && free <= total
            ? (int)Math.Round(100d * (total.Value - free) / total.Value)
            : null;
    }

    /// <summary>The <c>btime</c> line of <c>/proc/stat</c>: when the system started, in seconds since 1970.</summary>
    public static DateTimeOffset? ParseBootTime(string? stat)
    {
        if (stat is null)
        {
            return null;
        }

        foreach (var line in stat.AsSpan().EnumerateLines())
        {
            if (line.StartsWith("btime ")
                && long.TryParse(line["btime ".Length..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            {
                return DateTimeOffset.FromUnixTimeSeconds(seconds);
            }
        }

        return null;
    }

    /// <summary>A file's text, or <see langword="null"/> where it cannot be read.</summary>
    internal static string? ReadText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static long? Kilobytes(ReadOnlySpan<char> value)
    {
        var text = value.Trim();
        if (text.EndsWith("kB"))
        {
            text = text[..^2].TrimEnd();
        }

        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var kilobytes) ? kilobytes : null;
    }
}
