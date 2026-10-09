using System.Text.Json;
using HADA.Core.Abstractions;
using HADA.Core.Entities;
using Microsoft.Extensions.Logging;

namespace HADA.Engine.WebSocket;

/// <summary>
/// The part that talks to the HADA integration for Home Assistant, which makes the computer a device with
/// entities of its own. The protocol is described in PROTOCOL.md of the integration's repository,
/// https://github.com/inowakowski/hada-homeassistant.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>hada/connect</c> names the computer and lists every entity it has; entities Home Assistant has
/// beyond those are removed there. Until the connection closes, the entities are shown as available.</item>
/// <item><c>hada/entities</c> sends the list again when it changed, and <c>hada/update</c> what an entity reports.</item>
/// <item>A Home Assistant without the integration answers <c>unknown_command</c>. That is an
/// <see cref="Issue"/>, not a failed connection, and is tried again every
/// <see cref="HaWebSocketOptions.IntegrationRetryInterval"/>, since the integration may be set up meanwhile.</item>
/// </list>
/// </remarks>
public sealed partial class HaWebSocketEngine
{
    /// <summary>The newest version of the protocol this engine speaks.</summary>
    private const int IntegrationProtocol = 1;

    /// <summary>The integration is not installed in Home Assistant.</summary>
    public const string IssueIntegrationMissing = "integration_missing";

    /// <summary>The integration is installed, but was not added under Devices &amp; services.</summary>
    public const string IssueIntegrationNotSetUp = "integration_not_set_up";

    /// <summary>The device id is another Home Assistant user's.</summary>
    public const string IssueIntegrationUnauthorized = "integration_unauthorized";

    /// <summary>The integration no longer speaks a protocol as old as this engine's.</summary>
    public const string IssueIntegrationUnsupported = "integration_unsupported";

    /// <summary>The integration refused for a reason this version does not know, or did not answer.</summary>
    public const string IssueIntegrationFailed = "integration_failed";

    private static readonly TimeSpan IntegrationTimeout = TimeSpan.FromSeconds(10);

    // Several entities registered in a row, as when the tray app connects, are sent as one list.
    private static readonly TimeSpan EntitiesSettleDelay = TimeSpan.FromMilliseconds(300);

    private readonly SemaphoreSlim _integrationSignal = new(0);
    private volatile bool _integrationConnected;
    private volatile string? _issue;
    private int _entitiesChanged;

    /// <inheritdoc/>
    public string? Issue => _issue;

    private bool UsesIntegration => _options.SensorMode == HomeAssistantSensorMode.Integration;

    /// <summary>
    /// Connects to the integration, sends the entities again whenever they changed, and tries anew while the
    /// integration is not there. Runs for as long as the connection to Home Assistant does.
    /// </summary>
    private async Task RunIntegrationAsync(HaConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!_integrationConnected)
                {
                    await ConnectIntegrationAsync(connection).ConfigureAwait(false);
                }
                else if (Interlocked.Exchange(ref _entitiesChanged, 0) == 1)
                {
                    await Task.Delay(EntitiesSettleDelay, cancellationToken).ConfigureAwait(false);
                    Interlocked.Exchange(ref _entitiesChanged, 0);
                    await SendEntitiesAsync(connection).ConfigureAwait(false);
                }

                await _integrationSignal
                    .WaitAsync(_integrationConnected ? Timeout.InfiniteTimeSpan : _options.IntegrationRetryInterval, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The connection to Home Assistant is ending.
        }
    }

    private void DisconnectIntegration()
    {
        _integrationConnected = false;
        _issue = null;
    }

    /// <summary>Has the list of entities sent again soon; called when one was added or removed.</summary>
    private void NotifyEntitiesChanged()
    {
        if (UsesIntegration)
        {
            Interlocked.Exchange(ref _entitiesChanged, 1);
            _integrationSignal.Release();
        }
    }

    private async Task ConnectIntegrationAsync(HaConnection connection)
    {
        // Whatever changes from here on is in the list that is about to be sent.
        Interlocked.Exchange(ref _entitiesChanged, 0);
        var answer = await RequestAsync(
                connection,
                id => new
                {
                    id,
                    type = "hada/connect",
                    protocol = IntegrationProtocol,
                    device = new
                    {
                        id = _deviceId,
                        name = _deviceName,
                        app_version = AppVersion,
                        os = OsName,
                        os_version = Environment.OSVersion.Version.ToString(),
                        model = Model,
                    },
                    entities = IntegrationEntities(),
                },
                IntegrationTimeout)
            .ConfigureAwait(false);

        if (answer is { } result && IsSuccess(result))
        {
            _integrationConnected = true;
            SetIssue(null);
            if (_logger.IsEnabled(LogLevel.Information))
            {
                var ignored = IgnoredEntities(result);
                LogIntegrationConnected(_logger, _deviceName, ignored);
            }

            // What was reported while the answer was on its way is not in the list that was sent.
            await SendAllStatesAsync(connection).ConfigureAwait(false);
            return;
        }

        SetIssue(answer is { } refusal ? ErrorCode(refusal) switch
        {
            "unknown_command" => IssueIntegrationMissing,
            "not_found" => IssueIntegrationNotSetUp,
            "unauthorized" => IssueIntegrationUnauthorized,
            "unsupported_protocol" => IssueIntegrationUnsupported,
            _ => IssueIntegrationFailed,
        }
        : IssueIntegrationFailed);
    }

    private async Task SendEntitiesAsync(HaConnection connection)
    {
        var answer = await RequestAsync(
                connection,
                id => new { id, type = "hada/entities", device_id = _deviceId, entities = IntegrationEntities() },
                IntegrationTimeout)
            .ConfigureAwait(false);
        CheckStillConnected(answer);
    }

    private Task SendAllStatesAsync(HaConnection connection)
    {
        var states = ExposedSensors().Select(IntegrationState).ToArray();
        return states.Length == 0 ? Task.CompletedTask : SendStatesAsync(connection, states);
    }

    /// <summary>Reports what one entity says now; nothing while the integration is not connected, as connecting sends it all.</summary>
    private Task SetIntegrationStateAsync(EntityDescriptor entity, string state, IReadOnlyDictionary<string, object?>? attributes)
    {
        if (!_integrationConnected || _connection is not { } connection)
        {
            return Task.CompletedTask;
        }

        // "unavailable" is not a state here but a flag of its own; the entity keeps what it last reported.
        var update = new Dictionary<string, object?> { ["id"] = entity.Id, ["available"] = state != Unavailable };
        if (state != Unavailable)
        {
            update["state"] = SensorValue(entity, state);
            update["attributes"] = attributes ?? new Dictionary<string, object?>();
        }

        return SendStatesAsync(connection, [update]);
    }

    private async Task SendStatesAsync(HaConnection connection, Dictionary<string, object?>[] states)
    {
        var answer = await RequestAsync(
                connection, id => new { id, type = "hada/update", device_id = _deviceId, states }, IntegrationTimeout)
            .ConfigureAwait(false);
        CheckStillConnected(answer);
    }

    /// <summary>
    /// The integration answers "not found" once it no longer has this connection as the device's: it was
    /// reloaded or removed in Home Assistant. Connecting anew puts that right, or says what is wrong.
    /// </summary>
    private void CheckStillConnected(JsonElement? answer)
    {
        if (answer is { } result && !IsSuccess(result) && ErrorCode(result) is "not_found" or "unknown_command")
        {
            _integrationConnected = false;
            _integrationSignal.Release();
        }
    }

    private IEnumerable<EntityDescriptor> ExposedSensors() =>
        _registry.Entities.Where(entity => entity.Kind.ReportsState() && _filter.IsEnabled(entity));

    /// <summary>Every entity the computer has, each with what it reports now: the complete list the protocol asks for.</summary>
    private Dictionary<string, object?>[] IntegrationEntities() =>
    [
        .. ExposedSensors().Select(entity =>
        {
            var described = IntegrationState(entity);

            // A switch is shown as what it reports, a binary sensor, and a number as a sensor, until the
            // integration can be told to press, switch and set.
            described["kind"] = entity.Kind.IsBinary() ? "binary_sensor" : "sensor";
            described["name"] = entity.Name;
            described["icon"] = entity.Icon;
            described["device_class"] = entity.DeviceClass;
            described["unit"] = entity.UnitOfMeasurement;
            described["state_class"] = entity.Kind.IsBinary() ? null : entity.StateClass;
            return described;
        }),
    ];

    private Dictionary<string, object?> IntegrationState(EntityDescriptor entity)
    {
        var state = new Dictionary<string, object?> { ["id"] = entity.Id, ["available"] = _registry.IsAvailable(entity.Id) };
        if (_lastReadings.TryGetValue(entity.Id, out var reading))
        {
            state["state"] = SensorValue(entity, reading.State);
            state["attributes"] = reading.Attributes ?? new Dictionary<string, object?>();
        }

        return state;
    }

    /// <summary>Remembers what is wrong, and says so in the log when it changes, not at every retry.</summary>
    private void SetIssue(string? issue)
    {
        if (_issue == issue)
        {
            return;
        }

        _issue = issue;
        if (issue is not null)
        {
            LogIntegrationIssue(_logger, issue, _options.IntegrationRetryInterval);
        }
    }

    private static bool IsSuccess(JsonElement result) =>
        result.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True;

    private static string? ErrorCode(JsonElement result) =>
        result.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
        && error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String
            ? code.GetString()
            : null;

    private static string IgnoredEntities(JsonElement result) =>
        result.TryGetProperty("result", out var body) && body.ValueKind == JsonValueKind.Object
        && body.TryGetProperty("ignored", out var ignored) && ignored.ValueKind == JsonValueKind.Array && ignored.GetArrayLength() > 0
            ? string.Join(", ", ignored.EnumerateArray().Select(id => id.GetString()))
            : "none";

    [LoggerMessage(Level = LogLevel.Information, Message = "Connected to the HADA integration as '{DeviceName}'; entities it left out: {Ignored}.")]
    private static partial void LogIntegrationConnected(ILogger logger, string deviceName, string ignored);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The HADA integration cannot be used on this connection ({Issue}), so the computer's entities are not in Home Assistant; trying again every {RetryInterval}.")]
    private static partial void LogIntegrationIssue(ILogger logger, string issue, TimeSpan retryInterval);
}
