using System.Runtime.Versioning;
using HADA.Core;
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
    public static ISecretProtector ForThisSystem(bool currentUserOnly) => OperatingSystem.IsWindows()
        ? new DpapiSecretProtector(currentUserOnly ? DataProtectionScope.CurrentUser : DataProtectionScope.LocalMachine)
        : new KeyFileSecretProtector(Path.Combine(AppFolders.UserData, KeyFileSecretProtector.FileName));
}

/// <summary>
/// Where the system has no protection of its own for HADA to use yet: AES-GCM with a key of the user's own, in a
/// file only that user may read, kept apart from the settings. A copy of the settings file alone is useless.
/// </summary>
public sealed class KeyFileSecretProtector(string keyFile) : ISecretProtector
{
    public const string FileName = "secret.key";

    private const string Prefix = "k1:";
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly Lock _lock = new();
    private byte[]? _key;

    public string Protect(string secret)
    {
        var plain = Encoding.UTF8.GetBytes(secret);
        var sealedSecret = new byte[NonceSize + TagSize + plain.Length];
        var nonce = sealedSecret.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(Key(create: true)!, TagSize);
        aes.Encrypt(nonce, plain, sealedSecret.AsSpan(NonceSize + TagSize), sealedSecret.AsSpan(NonceSize, TagSize));
        return Prefix + Convert.ToBase64String(sealedSecret);
    }

    public string? TryUnprotect(string? protectedSecret)
    {
        if (string.IsNullOrEmpty(protectedSecret) || !protectedSecret.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var sealedSecret = Convert.FromBase64String(protectedSecret[Prefix.Length..]);
            if (sealedSecret.Length < NonceSize + TagSize || Key(create: false) is not { } key)
            {
                return null;
            }

            var plain = new byte[sealedSecret.Length - NonceSize - TagSize];
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(sealedSecret.AsSpan(0, NonceSize), sealedSecret.AsSpan(NonceSize + TagSize), sealedSecret.AsSpan(NonceSize, TagSize), plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }

    /// <summary>The key: read from its file, or made and written there the first time a secret is saved.</summary>
    private byte[]? Key(bool create)
    {
        lock (_lock)
        {
            if (_key is not null)
            {
                return _key;
            }

            if (File.Exists(keyFile))
            {
                var stored = File.ReadAllBytes(keyFile);
                return stored.Length == KeySize ? _key = stored : null;
            }

            if (!create)
            {
                return null;
            }

            AppFolders.CreatePrivate(Path.GetDirectoryName(keyFile)!);
            var key = RandomNumberGenerator.GetBytes(KeySize);
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using (var file = new FileStream(keyFile, options))
            {
                file.Write(key);
            }

            return _key = key;
        }
    }
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
