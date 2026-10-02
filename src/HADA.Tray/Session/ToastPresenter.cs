using System.IO;
using System.Net.Http;
using System.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace HADA.Tray.Session;

/// <summary>
/// Shows notifications as Windows toasts, which can carry a picture and buttons. A button that is pressed is
/// reported back through <paramref name="actionPressed"/> with the action the notification gave it.
/// </summary>
/// <remarks>
/// A program without an installer package has to tell Windows who it is before it may show toasts; that is the
/// registry entry written by <see cref="EnsureRegistered"/>. Windows reports a pressed button only to the process
/// that showed the toast and still holds on to it, so the toasts shown lately are kept.
/// </remarks>
/// <param name="fallback">Shows a plain notification, when a toast cannot be shown.</param>
public sealed partial class ToastPresenter(Action<string> actionPressed, Action<string, string> fallback, ILogger logger)
{
    /// <summary>Identifies HADA's notifications to Windows; the name shown is "HADA".</summary>
    public const string AppId = "HADA.Tray";

    private const int MaxKeptToasts = 20;
    private const long MaxImageBytes = 5 * 1024 * 1024;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private static readonly string DataFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HADA");

    private readonly List<ToastNotification> _shown = [];
    private bool _isRegistered;

    public async Task ShowAsync(NotificationRequest request)
    {
        try
        {
            EnsureRegistered();
            var imagePath = request.ImageUrl is null ? null : await TryDownloadImageAsync(request.ImageUrl);

            var document = new XmlDocument();
            document.LoadXml(BuildXml(request, imagePath));
            var toast = new ToastNotification(document);
            toast.Activated += (_, args) =>
            {
                // Pressing the notification itself carries no argument; only a button names an action.
                if (args is ToastActivatedEventArgs { Arguments: { Length: > 0 } action })
                {
                    actionPressed(action);
                }
            };

            lock (_shown)
            {
                _shown.Add(toast);
                if (_shown.Count > MaxKeptToasts)
                {
                    _shown.RemoveAt(0);
                }
            }

            ToastNotificationManager.CreateToastNotifier(AppId).Show(toast);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Whatever keeps a toast from showing, the user should still get the message.
            LogToastFailed(logger, ex.Message);
            fallback(request.Title, request.Message);
        }
    }

    /// <summary>The toast's markup: two lines of text, the picture below them, and one button per action.</summary>
    internal static string BuildXml(NotificationRequest request, string? imagePath)
    {
        var image = imagePath is null ? string.Empty : $"<image src=\"{SecurityElement.Escape(new Uri(imagePath).AbsoluteUri)}\"/>";
        var buttons = string.Concat(request.Buttons.Select(button =>
            $"<action content=\"{SecurityElement.Escape(button.Title)}\" arguments=\"{SecurityElement.Escape(button.Action)}\" activationType=\"foreground\"/>"));
        return "<toast><visual><binding template=\"ToastGeneric\">"
            + $"<text>{SecurityElement.Escape(request.Title)}</text><text>{SecurityElement.Escape(request.Message)}</text>{image}"
            + "</binding></visual>"
            + (buttons.Length > 0 ? $"<actions>{buttons}</actions>" : string.Empty)
            + "</toast>";
    }

    /// <summary>
    /// Tells Windows who shows these notifications. Called once when the tray starts, on its own thread, since the
    /// icon comes out of the program's resources; showing a notification makes up for it if that did not happen.
    /// </summary>
    public void EnsureRegistered()
    {
        if (_isRegistered)
        {
            return;
        }

        // Windows wants the icon as a file; the one in this program's resources is written out once.
        Directory.CreateDirectory(DataFolder);
        var iconPath = Path.Combine(DataFolder, "hada.ico");
        if (!File.Exists(iconPath))
        {
            using var resource = System.Windows.Application.GetResourceStream(AppIcon.Uri)!.Stream;
            using var file = File.Create(iconPath);
            resource.CopyTo(file);
        }

        using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{AppId}");
        key.SetValue("DisplayName", "HADA", RegistryValueKind.String);
        key.SetValue("IconUri", iconPath, RegistryValueKind.String);
        _isRegistered = true;
    }

    /// <summary>
    /// Toasts of a program like this one can only show pictures that are files on this computer. Returns
    /// <see langword="null"/> when the picture cannot be fetched; the notification is then shown without it.
    /// </summary>
    private async Task<string?> TryDownloadImageAsync(string url)
    {
        try
        {
            var folder = Path.Combine(DataFolder, "notifications");
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
            LogImageFailed(logger, ex.Message);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A notification could not be shown as a toast ({Reason}); showing it the plain way.")]
    private static partial void LogToastFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "The picture of a notification could not be fetched: {Reason}")]
    private static partial void LogImageFailed(ILogger logger, string reason);
}
