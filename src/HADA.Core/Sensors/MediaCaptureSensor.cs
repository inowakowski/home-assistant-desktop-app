using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Hosting;
using HADA.Core.Messaging;
using Microsoft.Extensions.Hosting;

namespace HADA.Core.Sensors;

/// <summary>
/// Publishes whether the microphone and the camera are in use, with the apps using them as an attribute.
/// Runs in the user's session. Left out where the system does not tell.
/// </summary>
public sealed class MediaCaptureSensor(IEventBus bus, IEntityRegistry registry, ISessionReadings session) : EagerBackgroundService
{
    public const string MicrophoneEntityId = BuiltInEntityIds.MicrophoneInUse;
    public const string CameraEntityId = BuiltInEntityIds.CameraInUse;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (session.AppsUsing(CaptureDevice.Microphone) is null && session.AppsUsing(CaptureDevice.Camera) is null)
        {
            return;
        }

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
            await PublishAsync(publisher, MicrophoneEntityId, CaptureDevice.Microphone, stoppingToken);
            await PublishAsync(publisher, CameraEntityId, CaptureDevice.Camera, stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private ValueTask PublishAsync(ChangeOnlyPublisher publisher, string entityId, CaptureDevice device, CancellationToken cancellationToken)
    {
        if (session.AppsUsing(device) is not { } apps)
        {
            return ValueTask.CompletedTask;
        }

        return publisher.PublishAsync(
            entityId,
            BinaryState.From(apps.Count > 0),
            new Dictionary<string, object?> { ["apps"] = apps },
            cancellationToken);
    }
}
