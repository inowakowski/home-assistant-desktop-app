using System.Globalization;
using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Messaging;
using HADA.Core.Models;
using Microsoft.Extensions.Logging;

namespace HADA.Core.Actions;

/// <summary>
/// Lets Home Assistant set the volume of the default playback device and mute it or the default microphone.
/// Runs in the user's session, to which audio devices belong.
/// </summary>
/// <remarks>
/// These are the controls; the volume sensor and the microphone's stay as the plain sensors automations may
/// already use.
/// </remarks>
public sealed partial class AudioControl(IEventBus bus, IEntityRegistry registry, ILogger<AudioControl> logger, IAudioDevices audio)
    : CommandHandler(bus, registry, logger), IDisposable
{
    public const string VolumeEntityId = BuiltInEntityIds.VolumeLevel;
    public const string MuteEntityId = BuiltInEntityIds.AudioMute;
    public const string MicrophoneMuteEntityId = BuiltInEntityIds.MicrophoneMute;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly ChangeOnlyPublisher _publisher = new(bus);

    // The publisher is not thread-safe, and states are reported both on a timer and right after a command.
    private readonly SemaphoreSlim _reporting = new(1, 1);

    protected override IReadOnlyList<EntityDescriptor> Entities { get; } =
    [
        new()
        {
            Id = VolumeEntityId,
            Name = "Volume level",
            Kind = EntityKind.Number,
            Icon = "mdi:volume-high",
            UnitOfMeasurement = "%",
            Min = 0,
            Max = 100,
            Step = 1,
        },
        new() { Id = MuteEntityId, Name = "Mute", Kind = EntityKind.Switch, Icon = "mdi:volume-off" },
        new() { Id = MicrophoneMuteEntityId, Name = "Mute microphone", Kind = EntityKind.Switch, Icon = "mdi:microphone-off" },
    ];

    public override void Dispose()
    {
        _reporting.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    protected override async ValueTask HandleAsync(ActionCommand command, CancellationToken cancellationToken)
    {
        var done = command.ActionId switch
        {
            VolumeEntityId => double.TryParse(command.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)
                && audio.TrySetVolume((int)Math.Round(percent)),
            MuteEntityId => audio.TrySetMute(AudioEndpoint.Speakers, command.Value == BinaryState.On),
            _ => audio.TrySetMute(AudioEndpoint.Microphone, command.Value == BinaryState.On),
        };

        if (!done)
        {
            LogFailed(Logger, command.ActionId);
        }

        // Home Assistant shows the new state at once, instead of the old one until the next poll.
        await ReportAsync(cancellationToken);
    }

    protected override async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(PollInterval);
            do
            {
                await ReportAsync(cancellationToken);
            }
            while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task ReportAsync(CancellationToken cancellationToken)
    {
        await _reporting.WaitAsync(cancellationToken);
        try
        {
            // Without a device there is nothing to report; the last state stays until one is connected again.
            if (audio.Read(AudioEndpoint.Speakers) is { } speakers)
            {
                await _publisher.PublishAsync(
                    VolumeEntityId, speakers.VolumePercent.ToString(CultureInfo.InvariantCulture), cancellationToken: cancellationToken);
                await _publisher.PublishAsync(MuteEntityId, BinaryState.From(speakers.IsMuted), cancellationToken: cancellationToken);
            }

            if (audio.Read(AudioEndpoint.Microphone) is { } microphone)
            {
                await _publisher.PublishAsync(
                    MicrophoneMuteEntityId, BinaryState.From(microphone.IsMuted), cancellationToken: cancellationToken);
            }
        }
        finally
        {
            _reporting.Release();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "'{EntityId}' could not be set: there is no such audio device, or it refused.")]
    private static partial void LogFailed(ILogger logger, string entityId);
}
