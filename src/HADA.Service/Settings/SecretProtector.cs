using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace HADA.Service.Settings;

/// <summary>Encrypts the passwords and tokens in the settings file, so a copy of the file is useless elsewhere.</summary>
public interface ISecretProtector
{
    /// <summary>Returns the secret encrypted and base64-encoded.</summary>
    string Protect(string secret);

    /// <summary>Returns <see langword="null"/> when there is no secret or it cannot be decrypted here, e.g. on another machine.</summary>
    string? TryUnprotect(string? protectedSecret);
}

public static class SecretProtector
{
    /// <summary>The protection the operating system this process runs on offers.</summary>
    /// <param name="currentUserOnly">
    /// Whether only the current user can decrypt, as for a portable copy, or any process on this computer, which
    /// the installed Windows service needs because it runs as SYSTEM.
    /// </param>
    /// <exception cref="PlatformNotSupportedException">HADA cannot protect secrets on this operating system yet.</exception>
    public static ISecretProtector ForThisSystem(bool currentUserOnly) => OperatingSystem.IsWindows()
        ? new DpapiSecretProtector(currentUserOnly ? DataProtectionScope.CurrentUser : DataProtectionScope.LocalMachine)
        : throw new PlatformNotSupportedException("HADA cannot protect saved passwords on this operating system yet.");
}

/// <summary>Windows' own data protection (DPAPI), keyed to the computer or to the user.</summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector(DataProtectionScope scope) : ISecretProtector
{
    private static readonly byte[] Entropy = "HADA.Settings.v1"u8.ToArray();

    public string Protect(string secret) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, scope));

    public string? TryUnprotect(string? protectedSecret)
    {
        if (string.IsNullOrEmpty(protectedSecret))
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedSecret), Entropy, scope));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
