namespace HADA.Platform.Linux;

/// <summary>The session shown on the computer's own screen, as systemd-logind keeps it.</summary>
/// <param name="Id">logind's id of the session, which <c>loginctl</c> takes.</param>
/// <param name="UserName">Who it belongs to.</param>
/// <param name="IsGreeter">Whether it is the sign-in screen rather than someone's desktop.</param>
public readonly record struct ConsoleSession(string Id, string UserName, bool IsGreeter, bool IsLocked);

/// <summary>
/// Asks systemd-logind, through <c>loginctl</c>, which session is on the first seat: the screen and keyboard of
/// the computer itself. Asked by two sensors every couple of seconds, it answers both from one look.
/// </summary>
public sealed class LoginSessions(Func<string, string[], ToolResult?>? run = null)
{
    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(1);

    private readonly Func<string, string[], ToolResult?> _run = run ?? Tools.Run;
    private readonly Lock _lock = new();
    private (DateTime At, ConsoleSession? Session, bool Known) _last;

    /// <summary>
    /// The session on the screen; <see langword="null"/> while there is none, and also where logind cannot be
    /// asked, which <paramref name="known"/> tells apart.
    /// </summary>
    public ConsoleSession? Read(out bool known)
    {
        lock (_lock)
        {
            if (DateTime.UtcNow - _last.At > Fresh)
            {
                _last = (DateTime.UtcNow, Look(out var isKnown), isKnown);
            }

            known = _last.Known;
            return _last.Session;
        }
    }

    private ConsoleSession? Look(out bool known)
    {
        var seat = _run("loginctl", ["show-seat", "seat0", "--property=ActiveSession", "--value"]);
        if (seat is not { Succeeded: true })
        {
            known = false;
            return null;
        }

        known = true;
        var id = seat.Value.Output.Trim();
        if (id.Length == 0)
        {
            return null;
        }

        var session = _run("loginctl", ["show-session", id, "--property=Name", "--property=Class", "--property=LockedHint"]);
        return session is { Succeeded: true } ? Parse(id, session.Value.Output) : null;
    }

    /// <summary>Reads <c>loginctl show-session</c>'s <c>Name=</c>, <c>Class=</c> and <c>LockedHint=</c> lines.</summary>
    public static ConsoleSession Parse(string id, string properties)
    {
        string name = string.Empty;
        string sessionClass = string.Empty;
        var locked = false;
        foreach (var line in properties.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var value = line[(separator + 1)..];
            switch (line[..separator])
            {
                case "Name":
                    name = value;
                    break;
                case "Class":
                    sessionClass = value;
                    break;
                case "LockedHint":
                    locked = value == "yes";
                    break;
            }
        }

        var isGreeter = sessionClass is "greeter" or "lock-screen";
        return new ConsoleSession(id, isGreeter ? string.Empty : name, isGreeter, locked || isGreeter);
    }
}
