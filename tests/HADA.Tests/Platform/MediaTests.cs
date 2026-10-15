using System.Collections.Concurrent;
using HADA.Core.Abstractions;
using HADA.Core.Actions;
using HADA.Core.Entities;
using HADA.Core.Messaging;
using HADA.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace HADA.Tests.Platform;

/// <summary>The media player and the media buttons, whichever system's media controls they tell.</summary>
public sealed class MediaTests : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly ChannelEventBus _bus = new();
    private readonly EntityRegistry _registry;
    private readonly FakeMedia _media = new();

    public MediaTests() => _registry = new EntityRegistry(_bus);

    public ValueTask DisposeAsync() => _bus.DisposeAsync();

    [Fact]
    public async Task The_buttons_tell_the_player_what_their_name_says()
    {
        using var buttons = new MediaButtons(_bus, _registry, NullLogger<MediaButtons>.Instance, _media);
        await buttons.StartAsync(CancellationToken.None);

        Assert.Equal(
            [BuiltInEntityIds.MediaNext, BuiltInEntityIds.MediaPlayPause, BuiltInEntityIds.MediaPrevious, BuiltInEntityIds.MediaStop],
            _registry.Entities.Select(entity => entity.Id).Order(StringComparer.Ordinal));

        await _bus.PublishAsync(new ActionCommand { ActionId = BuiltInEntityIds.MediaPlayPause });
        await _bus.PublishAsync(new ActionCommand { ActionId = BuiltInEntityIds.MediaNext });

        await WaitUntilAsync(() => _media.Told.Count == 2);
        Assert.Equal([MediaCommand.PlayPause, MediaCommand.Next], _media.Told);

        await buttons.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task The_media_player_shows_what_plays_and_how_loud_and_takes_commands()
    {
        var audio = new FakeAudio();
        await using var readings = _bus.Subscribe<TelemetryEvent>();
        using var player = new MediaPlayerControl(_bus, _registry, NullLogger<MediaPlayerControl>.Instance, _media, audio);
        await player.StartAsync(CancellationToken.None);

        Assert.True(_registry.TryGet(MediaPlayerControl.EntityId, out var entity));
        Assert.Equal(EntityKind.MediaPlayer, entity.Kind);

        TelemetryEvent? first = null;
        await WaitUntilAsync(() => readings.TryRead(out first));
        Assert.Equal(MediaPlayback.Playing, first!.State);
        Assert.Equal("Song", first.Attributes["title"]);
        Assert.Equal(40, first.Attributes["volume"]);

        await _bus.PublishAsync(new ActionCommand { ActionId = MediaPlayerControl.EntityId, Value = MediaCommand.Pause });
        await _bus.PublishAsync(new ActionCommand
        {
            ActionId = MediaPlayerControl.EntityId,
            Value = MediaCommand.SetVolume,
            Parameters = new Dictionary<string, object?> { [MediaCommand.Value] = "65" },
        });

        await WaitUntilAsync(() => audio.Volume == 65);
        Assert.Equal([MediaCommand.Pause], _media.Told);

        await player.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Without_a_way_to_set_the_sound_the_media_player_still_plays()
    {
        using var player = new MediaPlayerControl(_bus, _registry, NullLogger<MediaPlayerControl>.Instance, _media);
        await player.StartAsync(CancellationToken.None);

        await _bus.PublishAsync(new ActionCommand
        {
            ActionId = MediaPlayerControl.EntityId,
            Value = MediaCommand.SetVolume,
            Parameters = new Dictionary<string, object?> { [MediaCommand.Value] = "65" },
        });
        await _bus.PublishAsync(new ActionCommand { ActionId = MediaPlayerControl.EntityId, Value = MediaCommand.Next });

        await WaitUntilAsync(() => _media.Told.Count == 1);
        Assert.Equal([MediaCommand.Next], _media.Told);

        await player.StopAsync(CancellationToken.None);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        while (!condition())
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    private sealed class FakeMedia : IMediaSessions
    {
        private readonly ConcurrentQueue<string> _told = new();

        public IReadOnlyList<string> Told => [.. _told];

        public Task<MediaPlayback> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new MediaPlayback(MediaPlayback.Playing, "Song", "Artist", "Album", "Player"));

        public Task<bool> ControlAsync(string action, CancellationToken cancellationToken)
        {
            _told.Enqueue(action);
            return Task.FromResult(true);
        }
    }

    private sealed class FakeAudio : IAudioDevices
    {
        public int Volume { get; private set; } = 40;

        public AudioLevel? Read(AudioEndpoint endpoint) => new AudioLevel(Volume, IsMuted: false);

        public string? ReadName(AudioEndpoint endpoint) => "Speakers";

        public bool TrySetVolume(int percent)
        {
            Volume = percent;
            return true;
        }

        public bool TrySetMute(AudioEndpoint endpoint, bool muted) => true;
    }
}
