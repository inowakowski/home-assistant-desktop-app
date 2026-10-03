using System.Security.Principal;
using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Messaging;
using HADA.Ipc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HADA.Tests.Ipc;

/// <summary>The settings window's control API over a real pipe, backed by a fake <see cref="IServiceControl"/>.</summary>
public sealed class IpcControlTests : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly IpcOptions _options = new()
    {
        PipeName = "HADA.Tests." + Guid.NewGuid().ToString("N"),
        ClientName = "test-tray",
        MinReconnectDelay = TimeSpan.FromMilliseconds(50),
    };

    private readonly ChannelEventBus _serviceBus = new();
    private readonly EntityRegistry _serviceRegistry;
    private readonly FakeServiceControl _control;
    private readonly IpcServer _server;
    private readonly ServiceControlClient _client;

    public IpcControlTests()
    {
        _serviceRegistry = new EntityRegistry(_serviceBus);
        _control = new FakeServiceControl(_serviceRegistry);
        _server = new IpcServer(_serviceBus, _serviceRegistry, Options.Create(_options), NullLogger<IpcServer>.Instance, _control);
        _client = new ServiceControlClient(new IpcOptions { PipeName = _options.PipeName, ClientName = "test-window" });
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        await _server.StopAsync(CancellationToken.None);
        _server.Dispose();
        await _serviceBus.DisposeAsync();
    }

    [Fact]
    public async Task Status_lists_connected_trays_and_the_entities_they_own()
    {
        await _server.StartAsync(CancellationToken.None);
        await using var trayBus = new ChannelEventBus();
        var trayRegistry = new EntityRegistry(trayBus);
        using var tray = new IpcClient(trayBus, trayRegistry, Options.Create(_options), NullLogger<IpcClient>.Instance);
        await tray.StartAsync(CancellationToken.None);
        await trayRegistry.RegisterAsync(new EntityDescriptor { Id = "audio_volume", Name = "Volume", Kind = EntityKind.Sensor });
        await WaitUntilAsync(() => _serviceRegistry.TryGet("audio_volume", out _));

        var status = await _client.GetStatusAsync();

        Assert.Equal(["test-tray"], status.SensorClients);
        Assert.Equal("test-tray", Assert.Single(status.Entities).Source);
        await tray.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Settings_and_logs_are_readable_without_elevation()
    {
        await _server.StartAsync(CancellationToken.None);

        var settings = await _client.GetSettingsAsync();
        var logs = await _client.GetLogsAsync(afterSequence: 0);

        Assert.Equal("broker.local", Assert.Single(settings.MqttServers).Settings.Host);
        Assert.Equal(FakeServiceControl.CustomSensor, Assert.Single(settings.CustomSensors));
        Assert.Equal("hello", Assert.Single(logs).Message);
        Assert.True(settings.Updates.IncludePrereleases);
    }

    [Fact]
    public async Task Looking_for_an_update_needs_no_elevation()
    {
        await _server.StartAsync(CancellationToken.None);

        var result = await _client.CheckForUpdateAsync();

        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, result.Outcome);
        Assert.Equal("9.9.9", result.LatestVersion);
    }

    [Fact]
    public async Task Saving_settings_requires_an_elevated_administrator()
    {
        await _server.StartAsync(CancellationToken.None);
        var update = new SettingsUpdate(
            [new MqttServerUpdate(FakeServiceControl.Mqtt, SecretUpdate.Unchanged)], [new HomeAssistantServerUpdate(FakeServiceControl.HomeAssistant, SecretUpdate.Unchanged)], [], [], [], new UpdateSettings());

        if (IsElevatedAdministrator())
        {
            Assert.True((await _client.SaveSettingsAsync(update)).Success);
            Assert.Equal(1, _control.SaveCalls);
        }
        else
        {
            var error = await Assert.ThrowsAsync<ServiceControlException>(() => _client.SaveSettingsAsync(update));
            Assert.Equal(IpcError.Unauthorized, error.Error);
            await Assert.ThrowsAsync<ServiceControlException>(() => _client.TestConnectionAsync(ConnectionTarget.Mqtt, update));
            Assert.Equal(0, _control.SaveCalls);
            Assert.Equal(0, _control.TestCalls);
        }
    }

    [Fact]
    public async Task A_portable_copys_service_lets_its_own_user_save_settings_without_elevation()
    {
        // The service of a portable copy runs as the user; what its settings can make it do, that user can do anyway.
        var options = new IpcOptions { PipeName = "HADA.Tests." + Guid.NewGuid().ToString("N"), ClientName = "ui", TrustSameUser = true };
        using var server = new IpcServer(_serviceBus, _serviceRegistry, Options.Create(options), NullLogger<IpcServer>.Instance, _control);
        await server.StartAsync(CancellationToken.None);
        await using var client = new ServiceControlClient(options);
        var before = _control.SaveCalls;

        var update = new SettingsUpdate(
            [new MqttServerUpdate(FakeServiceControl.Mqtt, SecretUpdate.Unchanged)], [new HomeAssistantServerUpdate(FakeServiceControl.HomeAssistant, SecretUpdate.Unchanged)], [], [], [], new UpdateSettings());
        Assert.True((await client.SaveSettingsAsync(update)).Success);
        Assert.True((await client.TestConnectionAsync(ConnectionTarget.Mqtt, update)).Success);

        Assert.Equal(before + 1, _control.SaveCalls);
        await server.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Client_reports_when_the_service_is_not_running()
    {
        await Assert.ThrowsAsync<ServiceUnavailableException>(() => _client.GetStatusAsync());
    }

    private static bool IsElevatedAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        while (!condition())
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    private sealed class FakeServiceControl(IEntityRegistry registry) : IServiceControl
    {
        public static readonly MqttSettings Mqtt = new("broker.local", 1883, false, "", "", "", "homeassistant", "hada") { Id = "default" };
        public static readonly HomeAssistantSettings HomeAssistant = new("", "", "", "hada_command") { Id = "default" };
        public static readonly CustomSensorDefinition CustomSensor = new()
        {
            Id = "game_running",
            Name = "Game running",
            Type = CustomSensorType.ProcessRunning,
            Value = "game",
            IntervalSeconds = 5,
        };

        public int SaveCalls { get; private set; }

        public int TestCalls { get; private set; }

        public Task<ServiceStatus> GetStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ServiceStatus(
                "1.0.0",
                DateTimeOffset.Now,
                [],
                [],
                [.. registry.Entities.Select(entity => new EntityStatus(entity, true, "service", null, null))]));

        public Task<SettingsSnapshot> GetSettingsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SettingsSnapshot(
                [new MqttServerSnapshot(Mqtt, false)], [new HomeAssistantServerSnapshot(HomeAssistant, false)], [], [CustomSensor], [], new UpdateSettings(IncludePrereleases: true)));

        public Task<OperationResult> SaveSettingsAsync(SettingsUpdate settings, CancellationToken cancellationToken)
        {
            SaveCalls++;
            return Task.FromResult(new OperationResult(true));
        }

        public Task<OperationResult> TestConnectionAsync(
            ConnectionTarget target, SettingsUpdate settings, string? serverId, CancellationToken cancellationToken)
        {
            TestCalls++;
            return Task.FromResult(new OperationResult(true));
        }

        public IReadOnlyList<LogEntry> GetLogs(long afterSequence, int maxCount) =>
            [new LogEntry(1, DateTimeOffset.Now, LogLevel.Information, "Test", "hello", null)];

        public Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new UpdateCheckResult(UpdateCheckOutcome.UpdateAvailable, DateTimeOffset.Now, "9.9.9", "https://example.com/v9.9.9"));
    }
}
