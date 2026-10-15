using System.Collections.Concurrent;
using HADA.Core.Abstractions;
using HADA.Core.Entities;
using Tmds.DBus.Protocol;

namespace HADA.Platform.Linux;

/// <summary>What a player says about itself through MPRIS.</summary>
/// <param name="BusName">The player's name on the bus, e.g. <c>org.mpris.MediaPlayer2.spotify</c>.</param>
/// <param name="Status">MPRIS's <c>Playing</c>, <c>Paused</c> or <c>Stopped</c>.</param>
/// <param name="Identity">The player's own name for itself, e.g. <c>Spotify</c> or <c>Mozilla Firefox</c>.</param>
public sealed record MprisPlayer(string BusName, string Status, string? Title, string? Artist, string? Album, string? Identity)
{
    public const string Playing = "Playing";
    public const string Paused = "Paused";
    public const string Stopped = "Stopped";

    /// <summary>The player's state the way the other systems put it.</summary>
    public MediaPlayback ToPlayback()
    {
        var status = Status switch
        {
            Playing => MediaPlayback.Playing,
            Paused => MediaPlayback.Paused,
            Stopped => MediaPlayback.Stopped,
            _ => MediaPlayback.Idle,
        };
        return new MediaPlayback(status, Title, Artist, Album, Identity ?? LinuxMedia.ShortName(BusName));
    }
}

/// <summary>
/// The media players of the user's session, through MPRIS: every Linux player that the desktop's own media
/// controls show (GNOME's, KDE's) offers itself on the session's D-Bus as <c>org.mpris.MediaPlayer2.&lt;name&gt;</c>,
/// says what it plays, and takes play, pause, next and the rest from whoever asks.
/// </summary>
/// <remarks>
/// Where several players are open, the one playing counts, as it does for the desktop's controls; while none plays,
/// the last one that did. The connection is opened on first use and opened again after the bus has gone away.
/// </remarks>
/// <param name="busAddress">The session bus to ask; the session's own by default. Replaced in tests.</param>
public sealed class LinuxMedia(string? busAddress = null) : IMediaSessions, IDisposable
{
    private const string Prefix = "org.mpris.MediaPlayer2.";
    private const string ObjectPath = "/org/mpris/MediaPlayer2";
    private const string RootInterface = "org.mpris.MediaPlayer2";
    private const string PlayerInterface = "org.mpris.MediaPlayer2.Player";
    private const string Properties = "org.freedesktop.DBus.Properties";

    // playerctl's daemon stands in for whichever player it last saw; that player is on the bus itself already.
    private const string PlayerctlDaemon = Prefix + "playerctld";

    // A player that hangs must not hold up the others, nor the sensor's next reading.
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _connecting = new(1, 1);
    private readonly ConcurrentDictionary<string, string?> _identities = new(StringComparer.Ordinal);
    private DBusConnection? _connection;
    private volatile string? _last;

    public async Task<MediaPlayback> ReadAsync(CancellationToken cancellationToken)
    {
        var connection = await ConnectAsync() ?? throw new InvalidOperationException("There is no session bus.");
        return await CurrentAsync(connection, cancellationToken) is { } player ? player.ToPlayback() : MediaPlayback.Nothing;
    }

    public async Task<bool> ControlAsync(string action, CancellationToken cancellationToken)
    {
        var member = action switch
        {
            MediaCommand.Play => "Play",
            MediaCommand.Pause => "Pause",
            MediaCommand.PlayPause => "PlayPause",
            MediaCommand.Next => "Next",
            MediaCommand.Previous => "Previous",
            MediaCommand.Stop => "Stop",
            _ => null,
        };
        if (member is null)
        {
            return false;
        }

        try
        {
            if (await ConnectAsync() is not { } connection || await CurrentAsync(connection, cancellationToken) is not { } player)
            {
                return false;
            }

            MessageBuffer message;
            using (var writer = connection.GetMessageWriter())
            {
                writer.WriteMethodCallHeader(player.BusName, ObjectPath, PlayerInterface, member, null, MessageFlags.None);
                message = writer.CreateMessage();
            }

            await Guard(connection.CallMethodAsync(message), cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is DBusErrorReplyException or TimeoutException || IsBusFailure(ex))
        {
            // A player that cannot do this, e.g. go back from its first track, or no bus to reach it by.
            return false;
        }
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _connection = null;
        _connecting.Dispose();
    }

    /// <summary>
    /// The player that counts: the one playing (the last one that did, where several do), else the last one that
    /// played, else one that is paused, else any.
    /// </summary>
    public static MprisPlayer? Choose(IReadOnlyList<MprisPlayer> players, string? last)
    {
        var playing = players.Where(player => player.Status == MprisPlayer.Playing).ToList();
        if (playing.Count > 0)
        {
            return playing.Find(player => player.BusName == last) ?? playing[0];
        }

        return players.FirstOrDefault(player => player.BusName == last)
            ?? players.FirstOrDefault(player => player.Status == MprisPlayer.Paused)
            ?? (players.Count > 0 ? players[0] : null);
    }

    /// <summary>The player's <c>org.mpris.MediaPlayer2.Player</c> properties, as <c>GetAll</c> answers.</summary>
    public static MprisPlayer ParsePlayer(string busName, IReadOnlyDictionary<string, VariantValue> properties, string? identity)
    {
        var status = properties.TryGetValue("PlaybackStatus", out var value) && Unwrap(value) is { Type: VariantValueType.String } text
            ? text.GetString()
            : MprisPlayer.Stopped;

        Dictionary<string, VariantValue> metadata = properties.TryGetValue("Metadata", out var raw) && Unwrap(raw) is { Type: VariantValueType.Dictionary, KeyType: VariantValueType.String } dictionary
            ? dictionary.GetDictionary<string, VariantValue>()
            : [];
        return new MprisPlayer(
            busName,
            status,
            Text(metadata, "xesam:title"),
            Text(metadata, "xesam:artist"),
            Text(metadata, "xesam:album"),
            string.IsNullOrWhiteSpace(identity) ? null : identity);
    }

    /// <summary>The player's own part of its bus name, without the instance some add: <c>org.mpris.MediaPlayer2.vlc.instance42</c> is <c>vlc</c>.</summary>
    public static string ShortName(string busName)
    {
        var name = busName.StartsWith(Prefix, StringComparison.Ordinal) ? busName[Prefix.Length..] : busName;
        var instance = name.IndexOf(".instance", StringComparison.Ordinal);
        return instance > 0 ? name[..instance] : name;
    }

    private static VariantValue Unwrap(VariantValue value) =>
        value.Type == VariantValueType.Variant ? Unwrap(value.GetVariantValue()) : value;

    /// <summary>A text from the metadata; the artists are a list in MPRIS, joined here, though some players send just one.</summary>
    private static string? Text(Dictionary<string, VariantValue> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var raw))
        {
            return null;
        }

        var value = Unwrap(raw);
        var text = value.Type switch
        {
            VariantValueType.String => value.GetString(),
            VariantValueType.Array when value.ItemType == VariantValueType.String => string.Join(", ", value.GetArray<string>().Where(item => !string.IsNullOrWhiteSpace(item))),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static async Task<T> Guard<T>(Task<T> call, CancellationToken cancellationToken) =>
        await call.WaitAsync(CallTimeout, cancellationToken);

    private static async Task Guard(Task call, CancellationToken cancellationToken) =>
        await call.WaitAsync(CallTimeout, cancellationToken);

    private static bool IsBusFailure(Exception ex) =>
        ex is DBusConnectionException or DBusConnectFailedException or DBusMessageException or DBusReadException
            or IOException or ObjectDisposedException;

    private async Task<MprisPlayer?> CurrentAsync(DBusConnection connection, CancellationToken cancellationToken)
    {
        string[] names;
        try
        {
            names = await Guard(connection.ListServicesAsync(), cancellationToken);
        }
        catch (Exception ex) when (IsBusFailure(ex))
        {
            // The bus went away, e.g. the desktop restarted: the next reading connects again.
            Disconnect(connection);
            throw;
        }

        // Players that closed are forgotten; a browser, for one, takes a new name for every start.
        foreach (var gone in _identities.Keys.Where(name => !names.Contains(name, StringComparer.Ordinal)))
        {
            _identities.TryRemove(gone, out _);
        }

        var players = new List<MprisPlayer>();
        foreach (var name in names.Where(name => name.StartsWith(Prefix, StringComparison.Ordinal) && name != PlayerctlDaemon).Order(StringComparer.Ordinal))
        {
            try
            {
                var properties = await Guard(GetAllAsync(connection, name, PlayerInterface), cancellationToken);
                players.Add(ParsePlayer(name, properties, await IdentityAsync(connection, name, cancellationToken)));
            }
            catch (Exception ex) when (ex is DBusErrorReplyException or TimeoutException)
            {
                // A player that closed since it was listed, or one that does not answer: the others still count.
            }
        }

        var current = Choose(players, _last);
        if (current is not null)
        {
            _last = current.BusName;
        }

        return current;
    }

    private async Task<string?> IdentityAsync(DBusConnection connection, string name, CancellationToken cancellationToken)
    {
        if (_identities.TryGetValue(name, out var known))
        {
            return known;
        }

        string? identity = null;
        try
        {
            var properties = await Guard(GetAllAsync(connection, name, RootInterface), cancellationToken);
            identity = properties.TryGetValue("Identity", out var value) && Unwrap(value) is { Type: VariantValueType.String } text ? text.GetString() : null;
        }
        catch (Exception ex) when (ex is DBusErrorReplyException or TimeoutException)
        {
            // Without its name the player is known by its bus name.
        }

        _identities[name] = identity;
        return identity;
    }

    private static Task<Dictionary<string, VariantValue>> GetAllAsync(DBusConnection connection, string name, string @interface)
    {
        MessageBuffer message;
        using (var writer = connection.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(name, ObjectPath, Properties, "GetAll", "s", MessageFlags.None);
            writer.WriteString(@interface);
            message = writer.CreateMessage();
        }

        return connection.CallMethodAsync(message, static (reply, _) => reply.GetBodyReader().ReadDictionaryOfStringToVariantValue(), null);
    }

    /// <summary>The session bus; <see langword="null"/> where there is none, as outside a desktop session.</summary>
    private async Task<DBusConnection?> ConnectAsync()
    {
        if (_connection is { } open)
        {
            return open;
        }

        await _connecting.WaitAsync();
        try
        {
            if (_connection is { } opened)
            {
                return opened;
            }

            if ((busAddress ?? DBusAddress.Session) is not { } address)
            {
                return null;
            }

            var connection = new DBusConnection(address);
            try
            {
                await connection.ConnectAsync();
            }
            catch
            {
                connection.Dispose();
                throw;
            }

            _connection = connection;
            return connection;
        }
        finally
        {
            _connecting.Release();
        }
    }

    private void Disconnect(DBusConnection connection)
    {
        if (Interlocked.CompareExchange(ref _connection, null, connection) == connection)
        {
            connection.Dispose();
            _identities.Clear();
        }
    }
}
