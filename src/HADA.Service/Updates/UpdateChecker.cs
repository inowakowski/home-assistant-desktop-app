using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Messaging;
using HADA.Ipc;
using HADA.Service.CustomSensors;
using HADA.Service.Settings;
using Microsoft.Extensions.Options;

namespace HADA.Service.Updates;

/// <summary>Reads GitHub's list of releases.</summary>
public static class ReleaseFeed
{
    /// <summary>
    /// The highest version among the published releases, pre-releases included: tags look like <c>v0.4.0</c>.
    /// Returns <see langword="null"/> when there is none, or the JSON is not a list of releases.
    /// </summary>
    public static (Version Version, string Url)? FindLatest(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            (Version Version, string Url)? latest = null;
            foreach (var release in document.RootElement.EnumerateArray())
            {
                if (release.ValueKind != JsonValueKind.Object
                    || (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
                    || !release.TryGetProperty("tag_name", out var tag)
                    || !release.TryGetProperty("html_url", out var url)
                    || ParseVersion(tag.GetString()) is not { } version
                    || url.GetString() is not { Length: > 0 } address)
                {
                    continue;
                }

                if (latest is null || version > latest.Value.Version)
                {
                    latest = (version, address);
                }
            }

            return latest;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Parses <c>v0.4.0</c>, <c>0.4.0</c> or <c>0.4.0+commit</c>; four-part versions are compared by their first three parts.</summary>
    public static Version? ParseVersion(string? text)
    {
        var core = text?.Trim().TrimStart('v', 'V').Split('+', '-')[0];
        return Version.TryParse(core, out var version) && version.Build >= 0
            ? new Version(version.Major, version.Minor, version.Build)
            : null;
    }
}

/// <summary>
/// Asks GitHub once a day whether a newer HADA was released, and reports it as the <c>update_available</c> binary
/// sensor and on the window's Overview page. Nothing is downloaded or installed.
/// </summary>
/// <remarks>Switching the entity off on the Entities page also stops the daily request.</remarks>
public sealed partial class UpdateChecker(
    IEventBus bus,
    IEntityRegistry registry,
    IOptionsMonitor<EntityOptions> entityOptions,
    ILogger<UpdateChecker> logger) : BackgroundService
{
    public const string EntityId = ReservedIds.UpdateAvailable;

    private const string ReleasesUrl = "https://api.github.com/repos/inowakowski/home-assistant-desktop-app/releases?per_page=30";

    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromHours(1);

    private static readonly EntityDescriptor Entity = new()
    {
        Id = EntityId,
        Name = "Update available",
        Kind = EntityKind.BinarySensor,
        Icon = "mdi:package-up",
        DeviceClass = "update",
    };

    private static readonly Version? Installed = ReleaseFeed.ParseVersion(
        typeof(UpdateChecker).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    private volatile UpdateInfo? _available;

    /// <summary>The newer version found by the last check, if any.</summary>
    public UpdateInfo? Available => _available;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await registry.RegisterAsync(Entity, stoppingToken);
        if (Installed is null)
        {
            return;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        // GitHub's API refuses requests without a user agent.
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("HADA", Installed.ToString()));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        var publisher = new ChangeOnlyPublisher(bus);
        var delay = FirstCheckDelay;
        try
        {
            while (true)
            {
                await Task.Delay(delay, stoppingToken);
                delay = CheckInterval;
                if (!entityOptions.CurrentValue.ToFilter().IsEnabled(Entity))
                {
                    _available = null;
                    continue;
                }

                try
                {
                    var latest = ReleaseFeed.FindLatest(await http.GetStringAsync(new Uri(ReleasesUrl), stoppingToken));
                    var isNewer = latest is { } found && found.Version > Installed;
                    var update = isNewer ? new UpdateInfo(latest!.Value.Version.ToString(), latest.Value.Url) : null;
                    _available = update;
                    if (update is not null)
                    {
                        LogUpdateAvailable(logger, update.Version, update.Url);
                    }

                    await publisher.PublishAsync(
                        EntityId,
                        BinaryState.From(isNewer),
                        new Dictionary<string, object?>
                        {
                            ["installed_version"] = Installed.ToString(),
                            ["latest_version"] = (latest?.Version ?? Installed).ToString(),
                            ["release_url"] = latest?.Url,
                        },
                        stoppingToken);
                }
                catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !stoppingToken.IsCancellationRequested))
                {
                    // Offline, or GitHub is having a moment; neither is worth more than a line in the log.
                    LogCheckFailed(logger, ex.Message);
                    delay = RetryInterval;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "HADA {Version} is available: {Url}")]
    private static partial void LogUpdateAvailable(ILogger logger, string version, string url);

    [LoggerMessage(Level = LogLevel.Information, Message = "Could not check for a newer version: {Reason} Trying again in an hour.")]
    private static partial void LogCheckFailed(ILogger logger, string reason);
}
