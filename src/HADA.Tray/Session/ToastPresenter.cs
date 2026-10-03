using System.IO;
using System.Net.Http;
using HADA.Core;
using HADA.Core.Entities;
using System.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace HADA.Tray.Session;

/// <summary>
/// Shows notifications as Windows toasts, which can carry a picture and buttons. A button that is pressed is
/// reported back through <paramref name="actionPressed"/> with the action the notification gave it, and the
/// engine the notification came through, so that the answer goes to the Home Assistant that asked.
/// </summary>
/// <remarks>
/// A program without an installer package has to tell Windows who it is before it may show toasts; that is the
/// registry entry written by <see cref="EnsureRegistered"/>. Windows reports a pressed button only to the process
/// that showed the toast and still holds on to it, so the toasts shown lately are kept.
/// </remarks>
/// <param name="fallback">Shows a plain notification, when a toast cannot be shown.</param>
public sealed partial class ToastPresenter(Action<string, string?> actionPressed, Action<string, string> fallback, ILogger logger)
{
    /// <summary>Identifies this copy's notifications to Windows; the name shown is "HADA".</summary>
    public static string AppId { get; } = "HADA.Tray" + AppInstance.Suffix;

    /// <summary>
    /// Marks what comes back from a pressed notification as an address to open rather than a button's action.
    /// An action has no colon in it, so the two cannot be mistaken.
    /// </summary>
    private const string AddressPrefix = "open:";

    private const int MaxKeptToasts = 20;
    private const long MaxImageBytes = 5 * 1024 * 1024;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private static readonly string DataFolder = TrayPaths.DataFolder;

    private readonly List<ToastNotification> _shown = [];
    private bool _isRegistered;

    public async Task ShowAsync(NotificationRequest request)
    {
        if (request.Clear)
        {
            Clear(request);
            return;
        }

        try
        {
            EnsureRegistered();
            var imagePath = request.ImageUrl is null ? null : await TryDownloadImageAsync(request.ImageUrl);

            var document = new XmlDocument();

            // With a browser chosen, Windows is not left to open addresses in the default one: they come back
            // here, as pressed buttons do.
            var browser = UserPreferences.NotificationBrowser;
            document.LoadXml(BuildXml(request, imagePath, openAddressesHere: browser.Length > 0));
            var toast = new ToastNotification(document);
            if (TagOf(request) is { } tag)
            {
                // With the same tag and group as one shown earlier, Windows replaces that one.
                toast.Tag = tag;
                toast.Group = GroupOf(request.Origin);
            }

            toast.Activated += (_, args) =>
            {
                // Pressing the notification itself carries no argument; only a button names an action.
                if (args is not ToastActivatedEventArgs { Arguments: { Length: > 0 } action })
                {
                    return;
                }

                if (action.StartsWith(AddressPrefix, StringComparison.Ordinal))
                {
                    Browsers.Open(action[AddressPrefix.Length..], UserPreferences.NotificationBrowser);
                }
                else
                {
                    actionPressed(action, request.Origin);
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

    /// <summary>
    /// Takes back the notification with the request's tag, from the screen and from the notification centre.
    /// One that is not there any more is nothing to complain about.
    /// </summary>
    private void Clear(NotificationRequest request)
    {
        if (TagOf(request) is not { } tag)
        {
            return;
        }

        try
        {
            ToastNotificationManager.History.Remove(tag, GroupOf(request.Origin), AppId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogClearFailed(logger, ex.Message);
        }
    }

    private static string? TagOf(NotificationRequest request) =>
        request.Tag is { Length: > 0 } tag ? (tag.Length > NotificationContent.MaxTagLength ? tag[..NotificationContent.MaxTagLength] : tag) : null;

    /// <summary>
    /// Tags are told apart by where they came from, so that two Home Assistants using the same tag do not replace
    /// or take back each other's notifications. Windows allows a group 64 characters; a name may be longer.
    /// </summary>
    internal static string GroupOf(string? origin) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(origin ?? string.Empty)))[..32];

    /// <summary>
    /// The toast's markup: two lines of text, the picture below them, and one button per action. Pressing it opens
    /// its address, if it has one; a button with an address opens that instead of being reported.
    /// </summary>
    /// <param name="openAddressesHere">
    /// False to let Windows open addresses, in the default browser; that works even after the tray app was
    /// restarted. True to have them come back to this process, which opens them in the browser the user chose.
    /// </param>
    internal static string BuildXml(NotificationRequest request, string? imagePath, bool openAddressesHere = false)
    {
        string Address(string url) => openAddressesHere
            ? $"\"{SecurityElement.Escape(AddressPrefix + url)}\" activationType=\"foreground\""
            : $"\"{SecurityElement.Escape(url)}\" activationType=\"protocol\"";

        var image = imagePath is null ? string.Empty : $"<image src=\"{SecurityElement.Escape(new Uri(imagePath).AbsoluteUri)}\"/>";
        var buttons = string.Concat(request.Buttons.Select(button => NotificationContent.ToHttpUrl(button.Uri) is { } uri
            ? $"<action content=\"{SecurityElement.Escape(button.Title)}\" arguments={Address(uri)}/>"
            : $"<action content=\"{SecurityElement.Escape(button.Title)}\" arguments=\"{SecurityElement.Escape(button.Action)}\" activationType=\"foreground\"/>"));

        // Windows keeps a reminder on the screen only if it has a button; one that dismisses it will do.
        if (request.Sticky && buttons.Length == 0)
        {
            buttons = "<action content=\"\" arguments=\"dismiss\" activationType=\"system\"/>";
        }

        // Only what a browser opens: the address comes from Home Assistant, and must not start anything else here.
        var launch = NotificationContent.ToHttpUrl(request.Url) is { } url
            ? $" launch={Address(url)}"
            : string.Empty;
        return $"<toast{launch}{(request.Sticky ? " scenario=\"reminder\"" : string.Empty)}><visual><binding template=\"ToastGeneric\">"
            + $"<text>{SecurityElement.Escape(request.Title)}</text><text>{SecurityElement.Escape(request.Message)}</text>{image}"
            + "</binding></visual>"
            + (request.Silent ? "<audio silent=\"true\"/>" : string.Empty)
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

    [LoggerMessage(Level = LogLevel.Debug, Message = "A notification could not be taken back: {Reason}")]
    private static partial void LogClearFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "The picture of a notification could not be fetched: {Reason}")]
    private static partial void LogImageFailed(ILogger logger, string reason);
}
