using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using HADA.Core.Entities;
using HADA.Core.Models;
using Microsoft.Extensions.Logging;

namespace HADA.Engine.WebSocket;

/// <summary>
/// The part that makes the computer one of Home Assistant's <c>mobile_app</c> devices, as a phone with the
/// companion app is, so that <c>notify.mobile_app_{device name}</c> reaches it.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Registering (<c>POST /api/mobile_app/registrations</c>, once) yields the id of a webhook. It is kept in
/// the <see cref="IMobileAppRegistrationStore"/>, as registering again would make a second device.</item>
/// <item>Notifications arrive over the WebSocket connection, after <c>mobile_app/push_notification_channel</c>
/// was sent with that id, and each is confirmed, or Home Assistant closes the channel.</item>
/// <item>A pressed button is reported through the webhook as the event <c>mobile_app_notification_action</c>,
/// the one automations written for phones listen to.</item>
/// </list>
/// </remarks>
public sealed partial class HaWebSocketEngine
{
    private const string AppId = "io.github.inowakowski.hada";
    private const string NotificationActionEvent = "mobile_app_notification_action";

    /// <summary>How long a picture's address stays good for after Home Assistant signed it; the tray fetches it at once.</summary>
    private const int SignedPathSeconds = 60;

    private static readonly TimeSpan SignTimeout = TimeSpan.FromSeconds(5);

    private static readonly string AppVersion =
        (typeof(HaWebSocketEngine).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
        .Split('+')[0];

    // Set while the connection is one the registration can be used on.
    private volatile MobileAppRegistration? _registration;

    // Set while the channel of the current connection is open.
    private volatile int _pushSubscriptionId;

    // The sensors registered as entities since the connection was made; the others are registered before they are updated.
    private readonly ConcurrentDictionary<string, byte> _sensorEntities = new(StringComparer.Ordinal);

    // Requests sent over the socket whose answers are awaited, by their ids.
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pendingRequests = new();

    // Notifications are shown one after another, in the order they arrived, and not on the thread that reads the
    // socket: showing one may wait for an answer that only that thread can read.
    private readonly Lock _notificationQueueLock = new();
    private Task _notificationQueue = Task.CompletedTask;

    private static bool UsesMobileApp(HaWebSocketOptions options) =>
        options.Notifications || options.SensorMode == HomeAssistantSensorMode.Entities;

    /// <summary>
    /// Whether the connection has something to do that the token of an ordinary user is enough for, so that a
    /// refused subscription to command events is something to do without rather than a failure.
    /// </summary>
    private static bool WorksWithoutAdministrator(HaWebSocketOptions options) =>
        UsesMobileApp(options) || options.SensorMode == HomeAssistantSensorMode.Integration;

    /// <summary>
    /// Registers if need be, and opens the channel notifications arrive on if they are wanted. A failure here costs
    /// the notifications and the sensor entities, not the connection: it is logged, and tried again when the
    /// connection is next made.
    /// </summary>
    private async Task ConnectMobileAppAsync(HaConnection connection, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(HandshakeTimeout * 2);
        try
        {
            var registration = await EnsureRegisteredAsync(timeout.Token).ConfigureAwait(false);
            if (_options.Notifications)
            {
                var (subscriptionId, error) = await SubscribeToPushAsync(connection, registration.WebhookId, timeout.Token).ConfigureAwait(false);
                if (error is not null)
                {
                    // Home Assistant no longer knows the registration, or it is another user's than the token's.
                    registration = await RegisterAsync(timeout.Token).ConfigureAwait(false);
                    (subscriptionId, error) = await SubscribeToPushAsync(connection, registration.WebhookId, timeout.Token).ConfigureAwait(false);
                }

                if (error is null)
                {
                    _pushSubscriptionId = subscriptionId;
                    LogNotificationsReady(_logger, _deviceName);
                }
                else
                {
                    LogMobileAppUnavailable(_logger, error);
                }
            }

            _registration = registration;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or JsonException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            LogMobileAppUnavailable(_logger, Describe(ex));
        }
    }

    private void DisconnectMobileApp()
    {
        _pushSubscriptionId = 0;
        _registration = null;
        _sensorEntities.Clear();
        foreach (var id in _pendingRequests.Keys)
        {
            if (_pendingRequests.TryRemove(id, out var pending))
            {
                pending.TrySetCanceled();
            }
        }
    }

    /// <summary>Whether the message is the answer to a request made with <see cref="RequestAsync"/>.</summary>
    private bool TryCompleteRequest(JsonElement message)
    {
        if (_pendingRequests.IsEmpty
            || !message.TryGetProperty("type", out var type) || type.GetString() != "result"
            || !message.TryGetProperty("id", out var id) || !id.TryGetInt32(out var number)
            || !_pendingRequests.TryRemove(number, out var pending))
        {
            return false;
        }

        pending.TrySetResult(message.Clone());
        return true;
    }

    /// <summary>Sends a command and waits for its result. Not to be awaited by the thread that reads the socket.</summary>
    /// <param name="message">Makes the command, given its id.</param>
    private async Task<JsonElement?> RequestAsync(HaConnection connection, Func<int, object> message, TimeSpan timeout)
    {
        var id = connection.NextId();
        var pending = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[id] = pending;
        try
        {
            await connection.SendAsync(message(id), _stoppingToken).ConfigureAwait(false);
            return await pending.Task.WaitAsync(timeout, _stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException
            or System.Net.WebSockets.WebSocketException or InvalidOperationException or ObjectDisposedException)
        {
            return null;
        }
        finally
        {
            _pendingRequests.TryRemove(id, out _);
        }
    }

    /// <summary>The saved registration, told what this version and device name are; a new one when there is none that fits.</summary>
    private async Task<MobileAppRegistration> EnsureRegisteredAsync(CancellationToken cancellationToken)
    {
        var saved = _registrations.Load(_serverId);
        if (saved is null || saved.BaseUrl != _baseUrl!.AbsoluteUri || saved.DeviceId != _deviceId)
        {
            return await RegisterAsync(cancellationToken).ConfigureAwait(false);
        }

        using var response = await PostToWebhookAsync(
                saved.WebhookId,
                new
                {
                    type = "update_registration",
                    data = new
                    {
                        app_version = AppVersion,
                        device_name = _deviceName,
                        manufacturer = Manufacturer,
                        model = Model,
                        os_version = Environment.OSVersion.Version.ToString(),
                        app_data = AppData,
                    },
                },
                cancellationToken)
            .ConfigureAwait(false);

        // Gone: the device was deleted in Home Assistant, which remembers that about its webhook. A webhook Home
        // Assistant never heard of is answered too, but with nothing; the registration itself comes back otherwise.
        return response.StatusCode == HttpStatusCode.Gone || !await IsJsonObjectAsync(response, cancellationToken).ConfigureAwait(false)
            ? await RegisterAsync(cancellationToken).ConfigureAwait(false)
            : saved;
    }

    /// <summary>With the channel named, Home Assistant offers the action that sends notifications; without, it does not.</summary>
    private Dictionary<string, object> AppData =>
        _options.Notifications ? new() { ["push_websocket_channel"] = true } : [];

    private static async Task<bool> IsJsonObjectAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        try
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
            return body.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<MobileAppRegistration> RegisterAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUrl!, "api/mobile_app/registrations"))
        {
            Content = JsonContent.Create(new
            {
                device_id = _deviceId,
                app_id = AppId,
                app_name = "HADA",
                app_version = AppVersion,
                device_name = _deviceName,
                manufacturer = Manufacturer,
                model = Model,
                os_name = OsName,
                os_version = Environment.OSVersion.Version.ToString(),
                supports_encryption = false,
                app_data = AppData,
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.AccessToken);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidDataException("this Home Assistant has no mobile_app integration (it is part of default_config)");
        }

        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
        if (!body.RootElement.TryGetProperty("webhook_id", out var webhookId) || webhookId.GetString() is not { Length: > 0 } id)
        {
            throw new InvalidDataException("Home Assistant answered the registration without a webhook id");
        }

        var registration = new MobileAppRegistration(id, _baseUrl!.AbsoluteUri, _deviceId);
        _registrations.Save(_serverId, registration);
        LogRegistered(_logger, _deviceName);
        return registration;
    }

    /// <summary>Returns the id notifications will arrive under, or why Home Assistant refused.</summary>
    private async Task<(int SubscriptionId, string? Error)> SubscribeToPushAsync(
        HaConnection connection, string webhookId, CancellationToken cancellationToken)
    {
        var id = connection.NextId();
        await connection.SendAsync(
                new { id, type = "mobile_app/push_notification_channel", webhook_id = webhookId, support_confirm = true }, cancellationToken)
            .ConfigureAwait(false);

        while (true)
        {
            using var message = await ReceiveRequiredAsync(connection, cancellationToken).ConfigureAwait(false);
            var root = message.RootElement;
            if (HaConnection.TypeOf(message) != "result" || !root.TryGetProperty("id", out var answered) || answered.GetInt32() != id)
            {
                // A command that arrived meanwhile.
                await HandleMessageAsync(root).ConfigureAwait(false);
                continue;
            }

            if (root.TryGetProperty("success", out var success) && success.GetBoolean())
            {
                return (id, null);
            }

            var error = root.TryGetProperty("error", out var details) && details.TryGetProperty("code", out var code)
                ? code.GetString()
                : null;
            return (0, error ?? "unknown_error");
        }
    }

    private bool IsPushNotification(JsonElement message) =>
        _pushSubscriptionId is var subscription and not 0
        && message.TryGetProperty("id", out var id) && id.TryGetInt32(out var number) && number == subscription;

    private async Task HandlePushNotificationAsync(JsonElement notification)
    {
        await ConfirmNotificationAsync(notification).ConfigureAwait(false);

        // The document the notification is part of is gone once this returns.
        var copy = notification.Clone();
        lock (_notificationQueueLock)
        {
            _notificationQueue = _notificationQueue
                .ContinueWith(_ => ShowNotificationAsync(copy), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default)
                .Unwrap();
        }
    }

    private async Task ConfirmNotificationAsync(JsonElement notification)
    {
        // Confirmed first, and whatever becomes of it here: unconfirmed, Home Assistant closes the channel.
        if (_connection is { } connection && _registration is { } registration
            && notification.TryGetProperty("hass_confirm_id", out var confirm) && confirm.ValueKind == JsonValueKind.String)
        {
            try
            {
                await connection.SendAsync(
                        new
                        {
                            id = connection.NextId(),
                            type = "mobile_app/push_notification_confirm",
                            webhook_id = registration.WebhookId,
                            confirm_id = confirm.GetString(),
                        },
                        _stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is System.Net.WebSockets.WebSocketException or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
            {
                // The connection is going down; the connection loop reports that.
            }
        }
    }

    private async Task ShowNotificationAsync(JsonElement notification)
    {
        try
        {
            // "clear_notification" without a tag names nothing to take back, and is not something to show either.
            if (!NotificationContent.TryReadMobileApp(notification, _baseUrl, out var message, out var parameters)
                || (message?.Trim() == NotificationContent.ClearMessage && parameters?.ContainsKey(NotificationContent.Clear) != true)
                || !TryGetExposed(BuiltInEntityIds.Notification, out var entity)
                || !CommandValue.TryNormalize(entity, message, out var value))
            {
                // Not the text: what Home Assistant tells the user is none of the log's business.
                LogIgnoredNotification(_logger);
                return;
            }

            if (parameters is not null && parameters.TryGetValue(NotificationContent.Image, out var image) && image is string imageUrl)
            {
                parameters[NotificationContent.Image] = await SignIfOwnAsync(imageUrl).ConfigureAwait(false);
            }

            var command = new ActionCommand { ActionId = entity.Id, Value = value, Origin = Name };
            await _bus.PublishAsync(parameters is null ? command : command with { Parameters = parameters }, _stoppingToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Engine is stopping.
        }
    }

    /// <summary>
    /// A picture at this Home Assistant, a camera's for instance, is only given to who is signed in. The tray app
    /// fetches the picture and has no access token, nor should it; so Home Assistant is asked to sign the address,
    /// which makes it good for a minute without one. Any other address, and one that could not be signed, is
    /// returned as it is.
    /// </summary>
    private async Task<string> SignIfOwnAsync(string imageUrl)
    {
        if (_connection is not { } connection
            || !Uri.TryCreate(imageUrl, UriKind.Absolute, out var url)
            || Uri.Compare(url, _baseUrl, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) != 0
            || url.AbsolutePath.StartsWith("/local/", StringComparison.Ordinal))
        {
            return imageUrl;
        }

        var answer = await RequestAsync(
                connection, id => new { id, type = "auth/sign_path", path = url.PathAndQuery, expires = SignedPathSeconds }, SignTimeout)
            .ConfigureAwait(false);
        return answer is { } result
            && result.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True
            && result.TryGetProperty("result", out var signed) && signed.ValueKind == JsonValueKind.Object
            && signed.TryGetProperty("path", out var path) && path.GetString() is { Length: > 0 } signedPath
            && signedPath.StartsWith('/') && !signedPath.StartsWith("//", StringComparison.Ordinal)
            ? new Uri(new Uri(url.GetLeftPart(UriPartial.Authority)), signedPath).AbsoluteUri
            : imageUrl;
    }

    /// <summary>Tells Home Assistant that a button of a notification was pressed, the way a phone does.</summary>
    private async Task FireNotificationActionAsync(string action, CancellationToken cancellationToken)
    {
        if (_registration is not { } registration)
        {
            return;
        }

        try
        {
            using var response = await PostToWebhookAsync(
                    registration.WebhookId,
                    new
                    {
                        type = "fire_event",
                        data = new { event_type = NotificationActionEvent, event_data = new { action, device_id = _deviceId } },
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            LogActionNotReported(_logger, ex);
        }
    }

    /// <summary>
    /// Writes a sensor as an entity of the mobile_app device. The first time since the connection was made it is
    /// registered, which also brings its name, icon and unit up to date; after that only its state is sent.
    /// </summary>
    private async Task SetSensorEntityAsync(
        EntityDescriptor entity, string state, IReadOnlyDictionary<string, object?>? attributes, CancellationToken cancellationToken)
    {
        if (_registration is not { } registration)
        {
            return;
        }

        try
        {
            if (_sensorEntities.ContainsKey(entity.Id))
            {
                using var response = await PostToWebhookAsync(
                        registration.WebhookId,
                        new { type = "update_sensor_states", data = new[] { SensorState(entity, state, attributes) } },
                        cancellationToken)
                    .ConfigureAwait(false);
                if ((await ReadUpdatedSensorsAsync(response, cancellationToken).ConfigureAwait(false)).Contains(entity.Id))
                {
                    return;
                }

                // Home Assistant does not know the sensor any more: it was deleted there. Register it again.
            }

            var sensor = SensorState(entity, state, attributes);
            sensor["name"] = entity.Name;
            sensor["unit_of_measurement"] = entity.UnitOfMeasurement;
            if (!await RegisterSensorAsync(registration, sensor, entity, withClasses: true, cancellationToken).ConfigureAwait(false))
            {
                // A device class Home Assistant does not know for this kind of entity makes it refuse all of it.
                await RegisterSensorAsync(registration, sensor, entity, withClasses: false, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            LogStateRequestFailed(_logger, ex, "POST", entity.Id);
        }
    }

    private async Task<bool> RegisterSensorAsync(
        MobileAppRegistration registration, Dictionary<string, object?> sensor, EntityDescriptor entity, bool withClasses, CancellationToken cancellationToken)
    {
        sensor["device_class"] = withClasses ? entity.DeviceClass : null;

        // Home Assistant takes a state class for sensors only.
        if (withClasses && !entity.Kind.IsBinary() && entity.StateClass is not null)
        {
            sensor["state_class"] = entity.StateClass;
        }
        else
        {
            sensor.Remove("state_class");
        }

        using var response = await PostToWebhookAsync(registration.WebhookId, new { type = "register_sensor", data = sensor }, cancellationToken)
            .ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            _sensorEntities[entity.Id] = 0;
        }

        return response.IsSuccessStatusCode;
    }

    /// <summary>
    /// Shows the sensor entities of these as unavailable, where Home Assistant has them; where it has not, nothing
    /// is made. For sensors that are switched off, removed, or no longer sent through this connection.
    /// </summary>
    private async Task MarkSensorEntitiesUnavailableAsync(IReadOnlyList<EntityDescriptor> entities, CancellationToken cancellationToken)
    {
        if (_registration is not { } registration || entities.Count == 0)
        {
            return;
        }

        try
        {
            using var response = await PostToWebhookAsync(
                    registration.WebhookId,
                    new { type = "update_sensor_states", data = entities.Select(entity => SensorState(entity, Unavailable, attributes: null)).ToArray() },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            LogStateRequestFailed(_logger, ex, "POST", entities[0].Id);
        }
    }

    /// <summary>What both registering and updating a sensor send: which one, its state and its attributes.</summary>
    private static Dictionary<string, object?> SensorState(
        EntityDescriptor entity, string state, IReadOnlyDictionary<string, object?>? attributes) => new()
        {
            // As with states: a switch is shown as what it reports, a binary sensor, and a number as a sensor.
            ["type"] = entity.Kind.IsBinary() ? "binary_sensor" : "sensor",
            ["unique_id"] = entity.Id,
            ["state"] = SensorValue(entity, state),
            ["attributes"] = attributes ?? new Dictionary<string, object?>(),
            ["icon"] = entity.Icon,
        };

    /// <summary>
    /// Home Assistant wants a truth value from a binary sensor and a number from a sensor that measures something;
    /// "unavailable" is understood as it is by both.
    /// </summary>
    private static object SensorValue(EntityDescriptor entity, string state)
    {
        if (state == Unavailable)
        {
            return state;
        }

        if (entity.Kind.IsBinary())
        {
            return state == BinaryState.On;
        }

        if ((entity.UnitOfMeasurement is not null || entity.StateClass is not null)
            && double.TryParse(state, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number)
            && double.IsFinite(number))
        {
            return number;
        }

        return state.Length > MaxStateLength ? state[..MaxStateLength] : state;
    }

    /// <summary>The ids of the sensors an update was accepted for.</summary>
    private static async Task<HashSet<string>> ReadUpdatedSensorsAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var updated = new HashSet<string>(StringComparer.Ordinal);
        if (!response.IsSuccessStatusCode)
        {
            return updated;
        }

        try
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
            if (body.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var sensor in body.RootElement.EnumerateObject())
                {
                    if (sensor.Value.ValueKind == JsonValueKind.Object
                        && sensor.Value.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True)
                    {
                        updated.Add(sensor.Name);
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Not an answer to tell anything from.
        }

        return updated;
    }

    /// <summary>The webhook's id is what authorizes the request; the access token is not sent along.</summary>
    private Task<HttpResponseMessage> PostToWebhookAsync(string webhookId, object body, CancellationToken cancellationToken) =>
        _http.PostAsync(new Uri(_baseUrl!, $"api/webhook/{Uri.EscapeDataString(webhookId)}"), JsonContent.Create(body), cancellationToken);

    private static string Manufacturer => "HADA";

    private static string Model => $"{OsName} computer ({RuntimeInformation.OSArchitecture})";

    private static string OsName =>
        OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsLinux() ? "Linux" : "Other";

    [LoggerMessage(Level = LogLevel.Information, Message = "Registered with Home Assistant's mobile_app integration as '{DeviceName}'.")]
    private static partial void LogRegistered(ILogger logger, string deviceName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Notifications sent to the mobile_app device '{DeviceName}' arrive here.")]
    private static partial void LogNotificationsReady(ILogger logger, string deviceName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Home Assistant's mobile_app integration cannot be used on this connection, so its notifications and sensor entities are missing: {Reason}.")]
    private static partial void LogMobileAppUnavailable(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "A notification from mobile_app was not shown: it has no message, takes another back, or notifications are switched off.")]
    private static partial void LogIgnoredNotification(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "The pressed button of a notification could not be reported through mobile_app.")]
    private static partial void LogActionNotReported(ILogger logger, Exception exception);
}
