using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using HADA.Core;
using HADA.Ipc;

namespace HADA.Tests.Ipc;

/// <summary>Which pipes a client takes for the service's.</summary>
public class PipeTrustTests
{
    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public async Task A_pipe_of_the_users_own_is_the_services_only_where_the_service_is_the_users()
    {
        // Made as the installed service's pipe would be by any program of this user, while the service is not running.
        var pipeName = "HADA.T." + Guid.NewGuid().ToString("N")[..12];
        using var server = CreateOwnedByCurrentUser(pipeName);
        using var client = PipeAccess.Current.CreateClientPipe(pipeName);
        var waiting = server.WaitForConnectionAsync();
        await client.ConnectAsync(5000);
        await waiting;

        // The installed tray app's service is SYSTEM, so a pipe of its own user is somebody posing as it.
        Assert.Throws<UnauthorizedAccessException>(() => PipeAccess.Current.EnsureTrustedServer(client, pipeName, trustSameUser: false));

        // A portable copy's service is a process of that very user.
        PipeAccess.Current.EnsureTrustedServer(client, pipeName, trustSameUser: true);
    }

    [Fact]
    public void Only_a_portable_copy_and_the_services_of_Linux_and_macOS_are_their_users_own()
    {
        // The tests run as the installed app (see PortableTests), so this is the installed app's rule.
        Assert.False(AppInstance.IsPortable);
        Assert.Equal(!OperatingSystem.IsWindows(), new IpcOptions().TrustSameUser);
    }

    [WindowsFact]
    public void The_tests_do_not_run_as_SYSTEM_so_they_see_the_portable_marker_rules_of_a_user()
    {
        // SYSTEM is the installed service, which never takes itself for a portable copy, whatever lies beside it.
        Assert.False(AppInstance.RunsAsSystem());
    }

    /// <summary>Owned by the current user explicitly: an elevated administrator's objects belong to the Administrators group otherwise.</summary>
    [SupportedOSPlatform("windows")]
    private static NamedPipeServerStream CreateOwnedByCurrentUser(string pipeName)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new PipeSecurity();
        security.SetOwner(identity.User!);
        security.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }
}
