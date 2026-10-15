using HADA.Core.Abstractions;

namespace HADA.Platform.Linux;

/// <summary>A mounted file system as .NET lists it.</summary>
/// <param name="MountPoint">Where it is mounted, such as <c>/</c> or <c>/home</c>.</param>
/// <param name="Format">The kind of file system, such as <c>ext4</c> or <c>btrfs</c>.</param>
public readonly record struct Mount(string MountPoint, string Format, DriveType Type, long TotalBytes, long FreeBytes);

/// <summary>The computer's own disks, among everything Linux has mounted.</summary>
public static class MountedDisks
{
    private const double Gigabyte = 1024d * 1024 * 1024;

    // Parts of the system that are mounted on their own but are no disk of the user's.
    private static readonly string[] SystemPlaces = ["/boot", "/efi", "/snap", "/var/lib/snapd", "/var/lib/docker", "/var/lib/containers", "/run", "/proc", "/sys", "/dev", "/tmp"];

    // File systems that are images, layers or views of other ones.
    private static readonly HashSet<string> NoDisk = new(StringComparer.Ordinal) { "squashfs", "overlay", "tmpfs", "devtmpfs", "ramfs", "efivarfs", "fuse.portal", "nsfs", "iso9660" };

    public static IReadOnlyList<DiskUsage> Read()
    {
        var mounts = new List<Mount>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady)
                {
                    mounts.Add(new Mount(drive.Name, drive.DriveFormat, drive.DriveType, drive.TotalSize, drive.TotalFreeSpace));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Gone between being listed and being read, or not ours to look at.
            }
        }

        return Choose(mounts);
    }

    /// <summary>
    /// The disks among <paramref name="mounts"/>. One file system mounted in several places, as the subvolumes of
    /// btrfs often are at <c>/</c> and <c>/home</c>, is one disk: it is reported where it is mounted highest.
    /// </summary>
    public static IReadOnlyList<DiskUsage> Choose(IEnumerable<Mount> mounts)
    {
        var disks = new List<DiskUsage>();
        var seen = new HashSet<(string, long, long)>();
        foreach (var mount in mounts.OrderBy(mount => mount.MountPoint.Length).ThenBy(mount => mount.MountPoint, StringComparer.Ordinal))
        {
            if (mount.Type != DriveType.Fixed
                || mount.TotalBytes <= 0
                || NoDisk.Contains(mount.Format)
                || Array.Exists(SystemPlaces, place => mount.MountPoint == place || mount.MountPoint.StartsWith(place + "/", StringComparison.Ordinal))
                || !seen.Add((mount.Format, mount.TotalBytes, mount.FreeBytes)))
            {
                continue;
            }

            var used = mount.TotalBytes - mount.FreeBytes;
            disks.Add(new DiskUsage(
                KeyFor(mount.MountPoint),
                mount.MountPoint,
                (int)Math.Round(100d * used / mount.TotalBytes),
                Math.Round(mount.FreeBytes / Gigabyte, 1),
                Math.Round(mount.TotalBytes / Gigabyte, 1)));
        }

        return disks;
    }

    /// <summary>
    /// What the entity id is made of: <c>root</c> for <c>/</c>, and the mount point's parts joined by underscores
    /// otherwise, <c>/mnt/Data 2</c> giving <c>mnt_data_2</c>. It starts with a letter, as ids must.
    /// </summary>
    public static string KeyFor(string mountPoint)
    {
        var parts = mountPoint.ToLowerInvariant()
            .Select(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') ? character : '_')
            .ToArray();
        var key = string.Join('_', new string(parts).Split('_', StringSplitOptions.RemoveEmptyEntries));
        return key.Length == 0 ? "root" : key[0] is >= 'a' and <= 'z' ? key : "disk_" + key;
    }
}
