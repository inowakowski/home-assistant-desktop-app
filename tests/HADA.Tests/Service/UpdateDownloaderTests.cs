using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using HADA.Core.Updates;

namespace HADA.Tests.Service;

/// <summary>Downloads from a local web server laid out like GitHub's release downloads.</summary>
public sealed class UpdateDownloaderTests : IAsyncLifetime
{
    private const string Version = "0.9.0";
    private const string InstallerName = "HADA-0.9.0-x64.msi";

    private readonly HttpListener _listener = new();
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly HttpClient _http = new();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "HADA.Tests." + Guid.NewGuid().ToString("N"));
    private readonly byte[] _installer = RandomNumberGenerator.GetBytes(300_000);
    private readonly Uri _downloads;
    private Task _serving = Task.CompletedTask;

    public UpdateDownloaderTests()
    {
        _downloads = new Uri($"http://localhost:{GetFreePort()}/releases/download/");
        _listener.Prefixes.Add(_downloads.GetLeftPart(UriPartial.Authority) + "/");
    }

    public Task InitializeAsync()
    {
        _listener.Start();
        _serving = ServeAsync();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _listener.Close();
        await _serving;
        _http.Dispose();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public async Task An_installer_that_matches_its_checksum_is_kept_and_cannot_be_swapped_while_held()
    {
        Publish(_installer, checksumOf: _installer);
        var reported = new List<double>();

        string path;
        using (var installer = await UpdateDownloader.DownloadAsync(
            _http, _downloads, Version, Architecture.X64, _folder, new SynchronousProgress(reported.Add)))
        {
            path = installer.Path;
            Assert.Equal(Path.Combine(_folder, InstallerName), path);

            // Readable, as Windows Installer needs it, but not replaceable.
            Assert.Equal(_installer, ReadShared(path));
            Assert.Throws<IOException>(() => File.WriteAllBytes(path, [1, 2, 3]));
            Assert.Throws<IOException>(() => File.Delete(path));
            Assert.Throws<IOException>(() => File.Move(path, path + ".moved"));
        }

        Assert.Equal(1, reported[^1]);
        Assert.Equal(reported.Order(), reported);

        // Let go of, it is an ordinary file again, and a later download may replace it.
        File.Delete(path);
    }

    [Fact]
    public async Task An_installer_that_does_not_match_its_checksum_is_deleted()
    {
        Publish(_installer, checksumOf: [1, 2, 3]);

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => UpdateDownloader.DownloadAsync(_http, _downloads, Version, Architecture.X64, _folder));

        Assert.Contains("checksum", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(_folder, InstallerName)));
    }

    [Fact]
    public async Task Without_a_published_checksum_nothing_is_downloaded()
    {
        // Only the ARM64 installer has a checksum; this computer asks for x64.
        _files[$"/releases/download/v{Version}/{HadaReleases.ChecksumsName}"] =
            Encoding.ASCII.GetBytes($"{Convert.ToHexString(SHA256.HashData(_installer))}  HADA-0.9.0-arm64.msi\n");
        _files[$"/releases/download/v{Version}/{InstallerName}"] = _installer;

        await Assert.ThrowsAsync<InvalidDataException>(
            () => UpdateDownloader.DownloadAsync(_http, _downloads, Version, Architecture.X64, _folder));

        Assert.False(Directory.Exists(_folder));
    }

    [Fact]
    public async Task A_release_that_is_not_there_is_reported_as_such()
    {
        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => UpdateDownloader.DownloadAsync(_http, _downloads, Version, Architecture.X64, _folder));

        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
    }

    [Theory]
    [InlineData("../../evil")]
    [InlineData("0.9.0/../../x")]
    [InlineData("v0.9.0")]
    [InlineData("0.9")]
    [InlineData("")]
    public async Task Anything_but_a_plain_version_is_refused_before_any_request(string version)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => UpdateDownloader.DownloadAsync(_http, _downloads, version, Architecture.X64, _folder));
    }

    [Fact]
    public void Checksum_listings_are_read_in_both_common_forms()
    {
        var hashA = new string('a', 64);
        var hashB = new string('B', 64);
        var checksums = UpdateDownloader.ParseChecksums(
            $"{hashA}  HADA-0.9.0-arm64.msi\r\n{hashB} *HADA-0.9.0-x64.msi\n\nnot a checksum line\n{new string('z', 64)}  bad-hash.msi\n");

        Assert.Equal(2, checksums.Count);
        Assert.Equal(hashA, checksums["HADA-0.9.0-arm64.msi"]);
        Assert.Equal(hashB, checksums["HADA-0.9.0-x64.msi"]);
    }

    [Theory]
    [InlineData(Architecture.Arm64, "HADA-1.2.3-arm64.msi")]
    [InlineData(Architecture.X64, "HADA-1.2.3-x64.msi")]
    [InlineData(Architecture.X86, "HADA-1.2.3-x64.msi")]
    public void Each_computer_gets_the_installer_for_its_processor(Architecture architecture, string expected)
    {
        Assert.Equal(expected, HadaReleases.InstallerName("1.2.3", architecture));
    }

    private void Publish(byte[] installer, byte[] checksumOf)
    {
        _files[$"/releases/download/v{Version}/{InstallerName}"] = installer;
        _files[$"/releases/download/v{Version}/{HadaReleases.ChecksumsName}"] =
            Encoding.ASCII.GetBytes($"{Convert.ToHexString(SHA256.HashData(checksumOf)).ToLowerInvariant()}  {InstallerName}\n");
    }

    private async Task ServeAsync()
    {
        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            using var response = context.Response;
            if (_files.TryGetValue(context.Request.Url!.AbsolutePath, out var content))
            {
                response.ContentLength64 = content.Length;
                await response.OutputStream.WriteAsync(content);
            }
            else
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
            }
        }
    }

    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>Calls back on the reporting thread, so the test sees every report before the download returns.</summary>
    private sealed class SynchronousProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
