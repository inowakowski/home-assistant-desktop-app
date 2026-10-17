using HADA.Core.Abstractions;
using HADA.Core.Unix;
using Tmds.DBus.Protocol;

namespace HADA.Platform.Linux;

/// <summary>
/// "Do not disturb" the way the desktop keeps it. GNOME's is notification banners switched off, a setting HADA
/// changes as the switch in its notification settings does. KDE's notification server holds notifications back
/// while any app asks it to; HADA asks for as long as it is told to, or until it ends.
/// </summary>
/// <param name="run">Runs a tool; replaced in tests.</param>
/// <param name="desktop">The session's <c>XDG_CURRENT_DESKTOP</c>; replaced in tests.</param>
/// <param name="busAddress">The session bus to ask; the session's own by default. Replaced in tests.</param>
public sealed class LinuxDoNotDisturb(Func<string, string[], ToolResult?>? run = null, string? desktop = null, string? busAddress = null)
    : IDoNotDisturb, IDisposable
{
    private const string Service = "org.freedesktop.Notifications";
    private const string ObjectPath = "/org/freedesktop/Notifications";

    private static readonly string[] GnomeBanners = ["org.gnome.desktop.notifications", "show-banners"];
    private static readonly TimeSpan BusTimeout = TimeSpan.FromSeconds(10);

    private readonly Func<string, string[], ToolResult?> _run = run ?? Tools.Run;
    private readonly string _desktop = desktop ?? Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? string.Empty;
    private readonly SemaphoreSlim _asking = new(1, 1);
    private Way? _way;
    private DBusConnection? _connection;
    private uint? _cookie;

    private enum Way
    {
        None,
        Gnome,
        Inhibit,
    }

    public bool CanSet => Chosen != Way.None;

    public bool? Read() => Chosen switch
    {
        Way.Gnome => Ask("gsettings", ["get", .. GnomeBanners]) is { } banners && DoNotDisturbSensor.ParseGsettingsBool(banners) is { } shown ? !shown : null,
        Way.Inhibit => Inhibited(),
        _ => null,
    };

    public async Task<string?> SetAsync(bool on, CancellationToken cancellationToken)
    {
        switch (Chosen)
        {
            case Way.Gnome:
                var result = _run("gsettings", ["set", .. GnomeBanners, on ? "false" : "true"]);
                return result is { Succeeded: true } ? null : Tools.Describe("gsettings", result);

            case Way.Inhibit:
                return on ? await InhibitAsync(cancellationToken) : await ReleaseAsync(cancellationToken);

            default:
                return "the desktop does not let its notifications be kept quiet.";
        }
    }

    public void Dispose()
    {
        // Closing the connection ends HADA's request, as the server forgets those of who left.
        Disconnect();
        _asking.Dispose();
    }

    private Way Chosen => _way ??= Choose();

    private Way Choose()
    {
        var desktops = _desktop.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (desktops.Contains("GNOME", StringComparer.OrdinalIgnoreCase)
            && Ask("gsettings", ["get", .. GnomeBanners]) is { } banners && DoNotDisturbSensor.ParseGsettingsBool(banners) is not null)
        {
            return Way.Gnome;
        }

        return Inhibited() is not null ? Way.Inhibit : Way.None;
    }

    private bool? Inhibited() =>
        Ask("busctl", ["--user", "get-property", Service, ObjectPath, Service, "Inhibited"]) is { } inhibited
            ? DoNotDisturbSensor.ParseBusctlBool(inhibited)
            : null;

    private async Task<string?> InhibitAsync(CancellationToken cancellationToken)
    {
        await _asking.WaitAsync(cancellationToken);
        try
        {
            if (_cookie is not null)
            {
                return null;
            }

            var connection = await ConnectAsync(cancellationToken);
            MessageBuffer message;
            using (var writer = connection.GetMessageWriter())
            {
                writer.WriteMethodCallHeader(Service, ObjectPath, Service, "Inhibit", "ssa{sv}", MessageFlags.None);
                writer.WriteString("hada");
                writer.WriteString("Home Assistant asked for quiet");
                writer.WriteDictionary(new Dictionary<string, VariantValue>());
                message = writer.CreateMessage();
            }

            _cookie = await connection.CallMethodAsync(message, static (reply, _) => reply.GetBodyReader().ReadUInt32(), null)
                .WaitAsync(BusTimeout, cancellationToken);
            return null;
        }
        catch (Exception ex) when (IsBusFailure(ex))
        {
            Disconnect();
            return $"the notification server did not hold notifications back: {ex.Message}";
        }
        finally
        {
            _asking.Release();
        }
    }

    private async Task<string?> ReleaseAsync(CancellationToken cancellationToken)
    {
        await _asking.WaitAsync(cancellationToken);
        try
        {
            if (_cookie is { } cookie && _connection is { } connection)
            {
                try
                {
                    MessageBuffer message;
                    using (var writer = connection.GetMessageWriter())
                    {
                        writer.WriteMethodCallHeader(Service, ObjectPath, Service, "UnInhibit", "u", MessageFlags.None);
                        writer.WriteUInt32(cookie);
                        message = writer.CreateMessage();
                    }

                    await connection.CallMethodAsync(message).WaitAsync(BusTimeout, cancellationToken);
                }
                catch (Exception ex) when (IsBusFailure(ex))
                {
                    // Gone with the connection, which ends the request as well.
                    Disconnect();
                }

                _cookie = null;
            }
        }
        finally
        {
            _asking.Release();
        }

        // Quiet still, it is the desktop's own, which only its switch ends.
        return Inhibited() == true ? "the desktop's own Do not disturb is on; it ends from the desktop's notifications." : null;
    }

    private async Task<DBusConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        if (_connection is { } open)
        {
            return open;
        }

        var address = busAddress ?? DBusAddress.Session ?? throw new DBusConnectionException("There is no session bus.");
        var connection = new DBusConnection(address);
        try
        {
            await connection.ConnectAsync().AsTask().WaitAsync(BusTimeout, cancellationToken);
        }
        catch
        {
            connection.Dispose();
            throw;
        }

        _connection = connection;
        return connection;
    }

    private void Disconnect()
    {
        Interlocked.Exchange(ref _connection, null)?.Dispose();
        _cookie = null;
    }

    private static bool IsBusFailure(Exception ex) =>
        ex is DBusConnectionException or DBusConnectFailedException or DBusErrorReplyException or DBusMessageException or DBusReadException
            or IOException or ObjectDisposedException or TimeoutException;

    private string? Ask(string tool, string[] arguments) =>
        _run(tool, arguments) is { Succeeded: true } result ? result.Output : null;
}
