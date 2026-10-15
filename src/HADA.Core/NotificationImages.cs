namespace HADA.Core;

/// <summary>
/// Fetches the picture of a notification, which the desktops of Windows and Linux show only from a file on this
/// computer. Kept for an hour, in the user's data folder.
/// </summary>
public static class NotificationImages
{
    private const long MaxImageBytes = 5 * 1024 * 1024;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>
    /// Returns the file the picture was saved to, or <see langword="null"/> when it cannot be fetched; the
    /// notification is then shown without it.
    /// </summary>
    /// <param name="failed">Told why, for the log.</param>
    public static async Task<string?> TryDownloadAsync(string url, Action<string>? failed = null)
    {
        try
        {
            var folder = Path.Combine(AppFolders.UserData, "notifications");
            Directory.CreateDirectory(folder);
            foreach (var old in Directory.EnumerateFiles(folder))
            {
                if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddHours(-1))
                {
                    File.Delete(old);
                }
            }

            using var response = await Http.GetAsync(new Uri(url), HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            var extension = response.Content.Headers.ContentType?.MediaType switch
            {
                "image/png" => ".png",
                "image/jpeg" => ".jpg",
                "image/gif" => ".gif",
                "image/bmp" => ".bmp",
                _ => null,
            };
            if (extension is null || response.Content.Headers.ContentLength > MaxImageBytes)
            {
                return null;
            }

            var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + extension);
            await using (var source = await response.Content.ReadAsStreamAsync())
            await using (var target = File.Create(path))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer)) > 0)
                {
                    // The server's word on the size is not relied on.
                    total += read;
                    if (total > MaxImageBytes)
                    {
                        target.Close();
                        File.Delete(path);
                        return null;
                    }

                    await target.WriteAsync(buffer.AsMemory(0, read));
                }
            }

            return path;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or UriFormatException)
        {
            failed?.Invoke(ex.Message);
            return null;
        }
    }
}
