using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Hosting;
using HADA.Core.Messaging;
using Microsoft.Extensions.Hosting;

namespace HADA.Platform.Windows.Sensors;

/// <summary>
/// Publishes whether the microphone and the camera are in use, with the apps using them as an attribute.
/// Runs in the tray app, because the usage history is kept per user.
/// </summary>
public sealed class MediaCaptureSensor(IEventBus bus, IEntityRegistry registry) : EagerBackgroundService
{
    public const string MicrophoneEntityId = BuiltInEntityIds.MicrophoneInUse;
    public const string CameraEntityId = BuiltInEntityIds.CameraInUse;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await registry.RegisterAsync(
            new EntityDescriptor
            {
                Id = MicrophoneEntityId,
                Name = "Microphone in use",
                Kind = EntityKind.BinarySensor,
                Icon = "mdi:microphone",
            },
            stoppingToken);
        await registry.RegisterAsync(
            new EntityDescriptor
            {
                Id = CameraEntityId,
                Name = "Camera in use",
                Kind = EntityKind.BinarySensor,
                Icon = "mdi:webcam",
            },
            stoppingToken);

        var publisher = new ChangeOnlyPublisher(bus);
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            await PublishAsync(publisher, MicrophoneEntityId, MediaCapture.Microphone, stoppingToken);
            await PublishAsync(publisher, CameraEntityId, MediaCapture.Camera, stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private static ValueTask PublishAsync(ChangeOnlyPublisher publisher, string entityId, string capability, CancellationToken cancellationToken)
    {
        var apps = MediaCapture.AppsUsing(capability);
        return publisher.PublishAsync(
            entityId,
            BinaryState.From(apps.Count > 0),
            new Dictionary<string, object?> { ["apps"] = apps },
            cancellationToken);
    }
}
