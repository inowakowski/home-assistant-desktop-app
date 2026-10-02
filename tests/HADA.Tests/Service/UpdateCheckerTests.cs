using System.Net;
using HADA.Core.Entities;
using HADA.Core.Messaging;
using HADA.Core.Models;
using HADA.Ipc;
using HADA.Service.Settings;
using HADA.Service.Updates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace HADA.Tests.Service;

/// <summary>The update check against a stand-in for GitHub's release list.</summary>
public sealed class UpdateCheckerTests : IAsyncDisposable
{
    private const string Newer = """
        [
          { "tag_name": "v99.1.0", "html_url": "https://example.com/v99.1.0", "draft": false, "prerelease": true },
          { "tag_name": "v99.0.0", "html_url": "https://example.com/v99.0.0", "draft": false, "prerelease": false },
          { "tag_name": "v0.0.1", "html_url": "https://example.com/v0.0.1", "draft": false, "prerelease": false }
        ]
        """;

    private const string Older = """[ { "tag_name": "v0.0.1", "html_url": "https://example.com/v0.0.1", "draft": false, "prerelease": false } ]""";

    private readonly ChannelEventBus _bus = new();
    private readonly EntityRegistry _registry;
    private readonly TestOptionsMonitor<UpdateOptions> _options = new(new UpdateOptions());
    private readonly FakeGitHub _gitHub = new();
    private readonly UpdateChecker _checker;

    public UpdateCheckerTests()
    {
        _registry = new EntityRegistry(_bus);
        _checker = new UpdateChecker(_bus, _registry, _options, NullLogger<UpdateChecker>.Instance, _gitHub);
    }

    public async ValueTask DisposeAsync()
    {
        _checker.Dispose();
        await _bus.DisposeAsync();
    }

    [Fact]
    public async Task A_newer_release_is_reported_to_the_window_and_as_the_sensor()
    {
        _gitHub.Respond(HttpStatusCode.OK, Newer);
        await using var readings = _bus.Subscribe<TelemetryEvent>();

        var result = await _checker.CheckAsync(CancellationToken.None);

        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, result.Outcome);
        Assert.Equal("99.1.0", result.LatestVersion);
        Assert.Equal(new UpdateInfo("99.1.0", "https://example.com/v99.1.0"), _checker.Available);
        Assert.Same(result, _checker.LastCheck);

        Assert.True(readings.TryRead(out var reading));
        Assert.Equal(UpdateChecker.EntityId, reading.SensorId);
        Assert.Equal(BinaryState.On, reading.State);
        Assert.Equal("99.1.0", reading.Attributes["latest_version"]);
        Assert.Equal(UpdateChecker.Installed.ToString(), reading.Attributes["installed_version"]);
    }

    [Fact]
    public async Task Test_versions_count_only_when_asked_for()
    {
        _gitHub.Respond(HttpStatusCode.OK, Newer);
        _options.Set(new UpdateOptions { IncludePrereleases = false });

        var result = await _checker.CheckAsync(CancellationToken.None);

        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, result.Outcome);
        Assert.Equal("99.0.0", result.LatestVersion);
    }

    [Fact]
    public async Task Nothing_newer_means_up_to_date_and_the_sensor_is_off()
    {
        _gitHub.Respond(HttpStatusCode.OK, Older);
        await using var readings = _bus.Subscribe<TelemetryEvent>();

        var result = await _checker.CheckAsync(CancellationToken.None);

        Assert.Equal(UpdateCheckOutcome.UpToDate, result.Outcome);
        Assert.Null(_checker.Available);
        Assert.True(readings.TryRead(out var reading));
        Assert.Equal(BinaryState.Off, reading.State);
    }

    [Fact]
    public async Task A_private_repository_is_told_apart_from_a_failure()
    {
        _gitHub.Respond(HttpStatusCode.NotFound, """{ "message": "Not Found" }""");
        await using var readings = _bus.Subscribe<TelemetryEvent>();

        var result = await _checker.CheckAsync(CancellationToken.None);

        Assert.Equal(UpdateCheckOutcome.ReleasesHidden, result.Outcome);
        Assert.Null(_checker.Available);

        // Off, not unknown: there is no newer version anybody could install.
        Assert.True(readings.TryRead(out var reading));
        Assert.Equal(BinaryState.Off, reading.State);
    }

    [Fact]
    public async Task A_failed_look_keeps_what_was_known_and_says_why()
    {
        _gitHub.Respond(HttpStatusCode.OK, Newer);
        await _checker.CheckAsync(CancellationToken.None);
        await using var readings = _bus.Subscribe<TelemetryEvent>();
        _gitHub.Respond(HttpStatusCode.ServiceUnavailable, "try later");

        // Changing what counts as a release makes the next look a real one, not an answer from memory.
        _options.Set(new UpdateOptions { IncludePrereleases = false });
        var result = await _checker.CheckAsync(CancellationToken.None);

        Assert.Equal(UpdateCheckOutcome.Failed, result.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
        Assert.Equal("99.1.0", _checker.Available?.Version);
        Assert.False(readings.TryRead(out _));
    }

    [Fact]
    public async Task Asking_again_at_once_does_not_ask_github_again()
    {
        _gitHub.Respond(HttpStatusCode.OK, Newer);

        var first = await _checker.CheckAsync(CancellationToken.None);
        var second = await _checker.CheckAsync(CancellationToken.None);
        var third = await _checker.CheckAsync(CancellationToken.None);

        Assert.Same(first, second);
        Assert.Same(first, third);
        Assert.Equal(1, _gitHub.Requests);
    }

    [Fact]
    public void Saved_update_settings_bind_back_from_configuration()
    {
        var data = StoredSettingsConfigurationProvider.ToConfiguration(
            new StoredSettings { Updates = new UpdateSettings(CheckAutomatically: false, IncludePrereleases: false) });

        var bound = new ConfigurationBuilder().AddInMemoryCollection(data).Build()
            .GetSection(UpdateOptions.SectionName).Get<UpdateOptions>();

        Assert.False(bound!.CheckAutomatically);
        Assert.False(bound.IncludePrereleases);

        // Settings saved by an older version have no such section; both then stay on.
        var defaults = new ConfigurationBuilder()
            .AddInMemoryCollection(StoredSettingsConfigurationProvider.ToConfiguration(new StoredSettings())).Build()
            .GetSection(UpdateOptions.SectionName).Get<UpdateOptions>() ?? new UpdateOptions();
        Assert.True(defaults.CheckAutomatically);
        Assert.True(defaults.IncludePrereleases);
    }

    /// <summary>Answers every request with whatever was set last, and counts them.</summary>
    private sealed class FakeGitHub : HttpMessageHandler
    {
        private HttpStatusCode _status = HttpStatusCode.OK;
        private string _body = "[]";
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        public void Respond(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            Assert.NotEmpty(request.Headers.UserAgent);
            return Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_body) });
        }
    }
}
