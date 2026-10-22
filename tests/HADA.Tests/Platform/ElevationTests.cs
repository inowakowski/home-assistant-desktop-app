using System.Diagnostics;
using System.Runtime.Versioning;
using HADA.Platform.Windows.Desktop;
using HADA.Platform.Windows.Sensors;

namespace HADA.Tests.Platform;

[SupportedOSPlatform("windows")]
public sealed class ElevationTests
{
    // The tests run as whoever started them, as the ordinary window does. A build agent may run them in a session
    // nobody is signed in to, where it cannot be told and counts as another account.
    [WindowsFact]
    public void A_process_of_the_user_signed_in_to_its_session_runs_as_that_user()
    {
        using var process = Process.GetCurrentProcess();
        var sessionUser = ConsoleSession.TryReadUserName((uint)process.SessionId);

        var expected = string.Equals(sessionUser, Environment.UserName, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(expected, Elevation.RunsAsSessionUser());
    }
}
