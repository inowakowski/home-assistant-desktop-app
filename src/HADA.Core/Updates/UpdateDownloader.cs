using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace HADA.Core.Updates;

/// <summary>Where HADA's releases are published.</summary>
public static partial class HadaReleases
{
    public const string Repository = "inowakowski/home-assistant-desktop-app";

    public const string ChecksumsName = "SHA256SUMS.txt";

    /// <summary>The project's page; release pages live below it.</summary>
    public static Uri Site { get; } = new($"https://github.com/{Repository}/");

    /// <summary>GitHub's list of releases, newest first.</summary>
    public static Uri Api { get; } = new($"https://api.github.com/repos/{Repository}/releases?per_page=30");

    /// <summary>Below this, <c>v0.4.0/HADA-0.4.0-x64.msi</c> and so on.</summary>
    public static Uri Downloads { get; } = new(Site, "releases/download/");

    /// <summary>Whether a text is a version as releases are numbered: three numbers, nothing else.</summary>
    public static bool IsVersion(string? text) => text is not null && VersionPattern().IsMatch(text);

    /// <summary>The installer for a computer: ARM64 Windows gets the ARM64 one, everything else x64.</summary>
    public static string InstallerName(string version, Architecture architecture) =>
        $"HADA-{version}-{(architecture == Architecture.Arm64 ? "arm64" : "x64")}.msi";

    [GeneratedRegex(@"^\d{1,5}\.\d{1,5}\.\d{1,5}$")]
    private static partial Regex VersionPattern();
}

/// <summary>
/// An installer that was downloaded and found to match its published checksum. Until disposed, nobody can
/// change, replace or delete the file, so what is started is what was checked.
/// </summary>
public sealed class DownloadedInstaller : IDisposable
{
    private readonly FileStream _lock;

    internal DownloadedInstaller(string path, FileStream readLock)
    {
        Path = path;
        _lock = readLock;
    }

    public string Path { get; }

    public void Dispose() => _lock.Dispose();
}

/// <summary>Downloads a release's installer and checks it against the checksums published with the release.</summary>
public static class UpdateDownloader
{
    private const int BufferSize = 81920;
    private const int Sha256HexLength = 64;

    private static readonly SearchValues<char> HexDigits = SearchValues.Create("0123456789abcdefABCDEF");

    /// <param name="downloads">Where releases are; <see cref="HadaReleases.Downloads"/> outside tests.</param>
    /// <param name="folder">Where to put the installer. Created when missing; an older download of the same name is replaced.</param>
    /// <param name="progress">Told the fraction downloaded, 0 to 1, when the server says how large the file is.</param>
    /// <exception cref="ArgumentException"><paramref name="version"/> is not a version.</exception>
    /// <exception cref="HttpRequestException">The release, its checksums or the installer cannot be fetched.</exception>
    /// <exception cref="InvalidDataException">The release lists no checksum for the installer, or the download does not match it.</exception>
    /// <exception cref="IOException">The installer cannot be written, e.g. because an earlier download is still in use.</exception>
    public static async Task<DownloadedInstaller> DownloadAsync(
        HttpClient http,
        Uri downloads,
        string version,
        Architecture architecture,
        string folder,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(downloads);

        // The version ends up in an address and a file name; anything but digits and dots has no business there.
        if (!HadaReleases.IsVersion(version))
        {
            throw new ArgumentException($"'{version}' is not a version.", nameof(version));
        }

        var name = HadaReleases.InstallerName(version, architecture);
        var release = new Uri(downloads, $"v{version}/");

        // Checksums first: without one there is nothing to check the download against, so no point in downloading.
        var checksums = ParseChecksums(
            await http.GetStringAsync(new Uri(release, HadaReleases.ChecksumsName), cancellationToken).ConfigureAwait(false));
        if (!checksums.TryGetValue(name, out var expected))
        {
            throw new InvalidDataException($"The release publishes no checksum for {name}.");
        }

        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        try
        {
            using (var response = await http
                .GetAsync(new Uri(release, name), HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength;
                var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using (source.ConfigureAwait(false))
                {
                    var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
                    await using (target.ConfigureAwait(false))
                    {
                        var buffer = new byte[BufferSize];
                        long written = 0;
                        int read;
                        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                        {
                            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                            written += read;
                            if (total is > 0)
                            {
                                progress?.Report((double)written / total.Value);
                            }
                        }
                    }
                }
            }

            // Opened for reading with writers shut out, hashed through that very handle, and kept open by the
            // result: the file cannot be swapped between the check and the installer starting.
            var readLock = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
            try
            {
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(readLock, cancellationToken).ConfigureAwait(false));
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"The downloaded {name} does not match its published checksum and was deleted.");
                }

                return new DownloadedInstaller(path, readLock);
            }
            catch
            {
                await readLock.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch
        {
            // Half a download, or one that failed the check, is of no use to anybody.
            TryDelete(path);
            throw;
        }
    }

    /// <summary>
    /// Reads a <c>sha256sum</c> listing: a hash, then the file name, optionally marked with <c>*</c>.
    /// Lines that are anything else are skipped.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseChecksums(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var checksums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf(' ', StringComparison.Ordinal);
            if (separator != Sha256HexLength || line.AsSpan(0, separator).ContainsAnyExcept(HexDigits))
            {
                continue;
            }

            var name = line[separator..].TrimStart().TrimStart('*');
            if (name.Length > 0)
            {
                checksums[name] = line[..separator];
            }
        }

        return checksums;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still held by an installer started from an earlier download; it is not this download's to delete.
        }
    }
}
