using HADA.Core.Abstractions;

namespace HADA.Platform.Windows.Sensors;

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
                    letter.ToString(),
                    char.ToUpperInvariant(letter).ToString(),
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
