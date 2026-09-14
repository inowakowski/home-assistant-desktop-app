using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace HADA.Ipc;

/// <summary>Access control for the session pipe, enforced on both ends.</summary>
internal static class IpcPipeSecurity
{
    /// <summary>
    /// Creates a server instance. SYSTEM, administrators and the account running the server (the service account,
    /// or the developer when run from a console) get full control. Interactive users may read and write, which
    /// does not include creating further instances, so they cannot pose as the server. Network access is denied.
    /// </summary>
    public static NamedPipeServerStream CreateServerPipe(string pipeName, int maxInstances, bool isFirstInstance)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new PipeSecurity();
        security.AddAccessRule(Rule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(Rule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl));
        security.AddAccessRule(Rule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl));
        security.AddAccessRule(Rule(identity.User!, PipeAccessRights.FullControl));
        security.AddAccessRule(Rule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite));

        // FirstPipeInstance fails if someone else already created a pipe with this name.
        var options = PipeOptions.Asynchronous | (isFirstInstance ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        return NamedPipeServerStreamAcl.Create(
            pipeName, PipeDirection.InOut, maxInstances, PipeTransmissionMode.Byte, options, 0, 0, security);
    }

    /// <summary>
    /// Refuses a pipe not created by SYSTEM, an administrator or the current user, so another account cannot
    /// create the pipe before the service starts and collect window titles.
    /// </summary>
    public static void EnsureTrustedServer(NamedPipeClientStream pipe)
    {
        var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        using var identity = WindowsIdentity.GetCurrent();

        var trusted = owner is not null
            && (owner.IsWellKnown(WellKnownSidType.LocalSystemSid)
                || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)
                || owner == identity.User);
        if (!trusted)
        {
            throw new UnauthorizedAccessException(
                $"The IPC pipe is owned by '{owner?.Value ?? "nobody"}', not by the service account; refusing to send session data.");
        }
    }

    private static PipeAccessRule Rule(IdentityReference identity, PipeAccessRights rights, AccessControlType type = AccessControlType.Allow) =>
        new(identity, rights, type);
}
