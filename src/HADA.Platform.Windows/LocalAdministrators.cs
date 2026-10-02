using System.Runtime.InteropServices;
using System.Security.Principal;

namespace HADA.Platform.Windows;

/// <summary>Who is in this computer's Administrators group.</summary>
public static partial class LocalAdministrators
{
    private const int Success = 0;
    private const int AllAtOnce = -1;

    private static readonly SecurityIdentifier Group = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    /// <summary>
    /// Whether the account or group is the Administrators group or one of its direct members. A member counts
    /// whether or not it is elevated at the moment: it is about who the account is, not what a program of it may do.
    /// </summary>
    public static bool Contains(SecurityIdentifier sid)
    {
        if (sid == Group)
        {
            return true;
        }

        // The group's name depends on the language of Windows ("Administratorzy" in Polish); its SID does not.
        var name = Group.Translate(typeof(NTAccount)).Value;
        name = name[(name.IndexOf('\\', StringComparison.Ordinal) + 1)..];

        nint resume = 0;
        if (NetLocalGroupGetMembers(null, name, 0, out var buffer, AllAtOnce, out var count, out _, ref resume) != Success)
        {
            return false;
        }

        try
        {
            for (var i = 0; i < count; i++)
            {
                // LOCALGROUP_MEMBERS_INFO_0 is nothing but a pointer to the member's SID.
                if (new SecurityIdentifier(Marshal.ReadIntPtr(buffer, i * nint.Size)) == sid)
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            _ = NetApiBufferFree(buffer);
        }
    }

    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NetLocalGroupGetMembers(
        string? serverName,
        string localGroupName,
        int level,
        out nint buffer,
        int preferredMaximumLength,
        out int entriesRead,
        out int totalEntries,
        ref nint resumeHandle);

    [LibraryImport("netapi32.dll")]
    private static partial int NetApiBufferFree(nint buffer);
}
