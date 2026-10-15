using System.Globalization;
using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Messaging;
using HADA.Core.Models;
using Microsoft.Extensions.Logging;

namespace HADA.Core.Actions;

/// <summary>
/// The computer as a media player: what is playing, how loud, and play, pause, next, previous, stop, volume and
/// mute in one entity, the way Home Assistant shows a television or a speaker. Only the HADA integration can show
/// such an entity; the separate buttons, the volume slider and the playback sensor stay for the other ways.
/// </summary>
/// <remarks>
/// Runs in the user's session, as media players and the sound belong to the signed-in user. Commands go to the
/// player the system's own media controls show; the volume is that of the default speakers, where the system has
/// a way to set it.
/// </remarks>
public sealed partial class MediaPlayerControl(
    IEventBus bus, IEntityRegistry registry, ILogger<MediaPlayerControl> logger, IMediaSessions media, IAudioDevices? audio = null)
    : CommandHandler(bus, registry, logger)
{
    public const string EntityId = BuiltInEntityIds.MediaPlayer;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly ChangeOnlyPublisher _publisher = new(bus);

    // The publisher is not thread-safe, and the state is reported both on a timer and right after a command.
    private readonly SemaphoreSlim _reporting = new(1, 1);

    protected override IReadOnlyList<EntityDescriptor> Entities { get; } = [BuiltInDescriptors.MediaPlayer];

    public override void Dispose()
    {
        _reporting.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    protected override async ValueTask HandleAsync(ActionCommand command, CancellationToken cancellationToken)
    {
        var argument = command.GetParameter(MediaCommand.Value);
        var done = command.Value switch
        {
            MediaCommand.SetVolume => int.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out var percent)
                && audio?.TrySetVolume(Math.Clamp(percent, 0, 100)) == true,
            MediaCommand.Mute => audio?.TrySetMute(AudioEndpoint.Speakers, argument == BinaryState.On) == true,
            { } action => await media.ControlAsync(action, cancellationToken),
            _ => false,
        };

        if (!done)
        {
            LogFailed(Logger, command.Value);
        }

        // Home Assistant shows the new state at once, instead of the old one until the next poll.
        await ReportAsync(cancellationToken);
    }

    protected override async Task RunAsync(CancellationToken cancellationToken)
    {
        // Leave the thread that starts the host before the first read: Windows Runtime calls must not run on it.
        await Task.Yield();
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
            MediaPlayback playback;
            try
            {
                playback = await media.ReadAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Nothing to say about what is playing; the volume is still worth reporting.
                playback = MediaPlayback.Nothing;
            }

            var sound = audio?.Read(AudioEndpoint.Speakers);
            await _publisher.PublishAsync(
                EntityId,
                playback.Status,
                new Dictionary<string, object?>
                {
                    ["title"] = playback.Title,
                    ["artist"] = playback.Artist,
                    ["album"] = playback.Album,
                    ["app"] = playback.App,
                    ["volume"] = sound?.VolumePercent,
                    ["muted"] = sound?.IsMuted,
                },
                cancellationToken);
        }
        finally
        {
            _reporting.Release();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The media player could not do '{Action}'.")]
    private static partial void LogFailed(ILogger logger, string? action);
}
