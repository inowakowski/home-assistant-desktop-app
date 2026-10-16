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

    private readonly Func<string, string[], ToolResult?> _run = run ?? Tools.Run;
    private readonly string _desktop = desktop ?? Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? string.Empty;
    private readonly Lock _lock = new();
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

    public string? Set(bool on)
    {
        switch (Chosen)
        {
            case Way.Gnome:
                var result = _run("gsettings", ["set", .. GnomeBanners, on ? "false" : "true"]);
                return result is { Succeeded: true } ? null : Tools.Describe("gsettings", result);

            case Way.Inhibit:
                return on ? Inhibit() : Release();

            default:
                return "the desktop does not let its notifications be kept quiet.";
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            // Closing the connection ends HADA's request, as the server forgets those of who left.
            _connection?.Dispose();
            _connection = null;
            _cookie = null;
        }
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

    private string? Inhibit()
    {
        lock (_lock)
        {
            if (_cookie is not null)
            {
                return null;
            }

            try
            {
                var connection = Connect();
                MessageBuffer message;
                using (var writer = connection.GetMessageWriter())
                {
                    writer.WriteMethodCallHeader(Service, ObjectPath, Service, "Inhibit", "ssa{sv}", MessageFlags.None);
                    writer.WriteString("hada");
                    writer.WriteString("Home Assistant asked for quiet");
                    writer.WriteDictionary(new Dictionary<string, VariantValue>());
                    message = writer.CreateMessage();
                }

                _cookie = connection.CallMethodAsync(message, static (reply, _) => reply.GetBodyReader().ReadUInt32(), null)
                    .WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                return null;
            }
            catch (Exception ex) when (IsBusFailure(ex))
            {
                Disconnect();
                return $"the notification server did not hold notifications back: {ex.Message}";
            }
        }
    }

    private string? Release()
    {
        lock (_lock)
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

                    connection.CallMethodAsync(message).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                }
                catch (Exception ex) when (IsBusFailure(ex))
                {
                    // Gone with the connection, which ends the request as well.
                    Disconnect();
                }

                _cookie = null;
            }
        }

        // Quiet still, it is the desktop's own, which only its switch ends.
        return Inhibited() == true ? "the desktop's own Do not disturb is on; it ends from the desktop's notifications." : null;
    }

    private DBusConnection Connect()
    {
        if (_connection is { } open)
        {
            return open;
        }

        var address = busAddress ?? DBusAddress.Session ?? throw new DBusConnectionException("There is no session bus.");
        var connection = new DBusConnection(address);
        try
        {
            connection.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
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
        _connection?.Dispose();
        _connection = null;
        _cookie = null;
    }

    private static bool IsBusFailure(Exception ex) =>
        ex is DBusConnectionException or DBusConnectFailedException or DBusErrorReplyException or DBusMessageException or DBusReadException
            or IOException or ObjectDisposedException or TimeoutException;

    private string? Ask(string tool, string[] arguments) =>
        _run(tool, arguments) is { Succeeded: true } result ? result.Output : null;
}
