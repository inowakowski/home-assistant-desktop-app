using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Hosting;
using HADA.Core.Messaging;
using HADA.Core.Updates;
using HADA.Ipc;
using HADA.Service.CustomSensors;
using HADA.Service.Settings;
using Microsoft.Extensions.Options;

namespace HADA.Service.Updates;

/// <summary>Reads GitHub's list of releases.</summary>
public static class ReleaseFeed
{
    /// <summary>
    /// The highest version among the published releases: tags look like <c>v0.4.0</c>. Drafts never count;
    /// pre-releases only when asked for. Returns <see langword="null"/> when there is none, or the JSON is not a
    /// list of releases.
    /// </summary>
    public static (Version Version, string Url)? FindLatest(string json, bool includePrereleases = true)
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
                    || IsTrue(release, "draft")
                    || (!includePrereleases && IsTrue(release, "prerelease"))
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

    private static bool IsTrue(JsonElement release, string property) =>
        release.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;
}

/// <summary>
/// Asks GitHub whether a newer HADA was released: once a day by itself, unless that is turned off in settings,
/// and whenever the window's "Check now" asks. Reports the answer as the <c>update_available</c> binary sensor and
/// to the window. The service itself downloads and installs nothing; the window offers to, when its user asks.
/// </summary>
public sealed partial class UpdateChecker : EagerBackgroundService
{
    public const string EntityId = BuiltInEntityIds.UpdateAvailable;

    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromHours(1);

    // Any signed-in user may press "Check now"; pressing it over and over must not become a stream of requests.
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(30);

    private static readonly EntityDescriptor Entity = new()
    {
        Id = EntityId,
        Name = "Update available",
        Kind = EntityKind.BinarySensor,
        Icon = "mdi:package-up",
        DeviceClass = "update",
    };

    private readonly IEntityRegistry _registry;
    private readonly IOptionsMonitor<UpdateOptions> _options;
    private readonly ILogger _logger;
    private readonly HttpClient _http;
    private readonly ChangeOnlyPublisher _publisher;
    private readonly SemaphoreSlim _checking = new(1, 1);
    private volatile UpdateCheckResult? _last;
    private volatile UpdateInfo? _available;
    private bool _lastIncludedPrereleases;
    private bool _releasesHidden;

    /// <param name="handler">Answers the requests in place of GitHub; for tests.</param>
    public UpdateChecker(
        IEventBus bus,
        IEntityRegistry registry,
        IOptionsMonitor<UpdateOptions> options,
        ILogger<UpdateChecker> logger,
        HttpMessageHandler? handler = null)
    {
        _registry = registry;
        _options = options;
        _logger = logger;
        _publisher = new ChangeOnlyPublisher(bus);
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(20);

        // GitHub's API refuses requests without a user agent.
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("HADA", Installed.ToString()));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    /// <summary>The version this service is.</summary>
    public static Version Installed { get; } =
        ReleaseFeed.ParseVersion(
            typeof(UpdateChecker).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion)
        ?? new Version(0, 0, 0);

    /// <summary>The newer version found by the last check, if any.</summary>
    public UpdateInfo? Available => _available;

    /// <summary>How the last check went, or <see langword="null"/> before the first one.</summary>
    public UpdateCheckResult? LastCheck => _last;

    /// <summary>
    /// Asks GitHub now. A check made less than half a minute ago, with the same settings, is answered from memory.
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        await _checking.WaitAsync(cancellationToken);
        try
        {
            var includePrereleases = _options.CurrentValue.IncludePrereleases;
            if (_last is { } recent
                && _lastIncludedPrereleases == includePrereleases
                && DateTimeOffset.UtcNow - recent.CheckedAt < MinimumInterval)
            {
                return recent;
            }

            var result = await FetchAsync(includePrereleases, cancellationToken);
            _last = result;
            _lastIncludedPrereleases = includePrereleases;
            if (result.Outcome == UpdateCheckOutcome.Failed)
            {
                // What was known before still stands; a failed look says nothing new.
                return result;
            }

            _available = result.Outcome == UpdateCheckOutcome.UpdateAvailable
                ? new UpdateInfo(result.LatestVersion!, result.Url!)
                : null;
            await _publisher.PublishAsync(
                EntityId,
                BinaryState.From(_available is not null),
                new Dictionary<string, object?>
                {
                    ["installed_version"] = Installed.ToString(),
                    ["latest_version"] = result.LatestVersion ?? Installed.ToString(),
                    ["release_url"] = result.Url,
                },
                cancellationToken);
            return result;
        }
        finally
        {
            _checking.Release();
        }
    }

    public override void Dispose()
    {
        _http.Dispose();
        _checking.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _registry.RegisterAsync(Entity, stoppingToken);

        // Turning the daily check on, or changing what counts as a release, should not wait a day to show.
        // Declared before the registration, so it outlives it.
        using var changed = new SemaphoreSlim(0);
        var applied = Fingerprint(_options.CurrentValue);
        using var registration = _options.OnChange(current =>
        {
            // Any saved setting reloads every option; only a change to these two is of interest here.
            if (Interlocked.Exchange(ref applied, Fingerprint(current)) != Fingerprint(current))
            {
                changed.Release();
            }
        });

        var delay = FirstCheckDelay;
        try
        {
            while (true)
            {
                await changed.WaitAsync(delay, stoppingToken);
                delay = CheckInterval;
                if (!_options.CurrentValue.CheckAutomatically)
                {
                    continue;
                }

                if ((await CheckAsync(stoppingToken)).Outcome == UpdateCheckOutcome.Failed)
                {
                    delay = RetryInterval;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private static int Fingerprint(UpdateOptions options) =>
        (options.CheckAutomatically ? 1 : 0) | (options.IncludePrereleases ? 2 : 0);

    private async Task<UpdateCheckResult> FetchAsync(bool includePrereleases, CancellationToken cancellationToken)
    {
        try
        {
            var latest = ReleaseFeed.FindLatest(await _http.GetStringAsync(HadaReleases.Api, cancellationToken), includePrereleases);
            _releasesHidden = false;
            if (latest is { } found && found.Version > Installed)
            {
                var newer = new UpdateCheckResult(UpdateCheckOutcome.UpdateAvailable, DateTimeOffset.UtcNow, found.Version.ToString(), found.Url);
                LogUpdateAvailable(_logger, newer.LatestVersion!, found.Url);
                return newer;
            }

            return new UpdateCheckResult(UpdateCheckOutcome.UpToDate, DateTimeOffset.UtcNow, latest?.Version.ToString(), latest?.Url);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // What GitHub answers for a private repository. Not a failure to retry within the hour, and not
            // worth a line in the log every day either.
            if (!_releasesHidden)
            {
                LogReleasesHidden(_logger);
                _releasesHidden = true;
            }

            return new UpdateCheckResult(UpdateCheckOutcome.ReleasesHidden, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Offline, or GitHub is having a moment; neither is worth more than a line in the log.
            LogCheckFailed(_logger, ex.Message);
            return new UpdateCheckResult(UpdateCheckOutcome.Failed, DateTimeOffset.UtcNow, Message: ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "HADA's releases cannot be seen without signing in to GitHub, so there is nothing to compare this version with.")]
    private static partial void LogReleasesHidden(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "HADA {Version} is available: {Url}")]
    private static partial void LogUpdateAvailable(ILogger logger, string version, string url);

    [LoggerMessage(Level = LogLevel.Information, Message = "Could not check for a newer version: {Reason}")]
    private static partial void LogCheckFailed(ILogger logger, string reason);
}
