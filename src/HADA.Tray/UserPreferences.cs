using System.IO;
using System.Security;
using Microsoft.Win32;

namespace HADA.Tray;

/// <summary>
/// Choices that belong to one user and need no administrator rights, kept under <c>HKCU\Software\HADA</c> next to
/// <see cref="Autostart"/>. Read by both the tray app and the window, which are separate processes.
/// </summary>
internal static class UserPreferences
{
    public const int DefaultIdleSeconds = 60;
    public const int MinIdleSeconds = 10;
    public const int MaxIdleSeconds = 3600;

    private const string KeyPath = @"Software\HADA";
    private const string LanguageValue = "Language";
    private const string IdleValue = "IdleSeconds";

    /// <summary><c>pl</c>, <c>en</c>, or empty to follow Windows' display language.</summary>
    public static string Language
    {
        get => Read(LanguageValue) is string language && language is "pl" or "en" ? language : string.Empty;
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            if (value is "pl" or "en")
            {
                key.SetValue(LanguageValue, value, RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(LanguageValue, throwOnMissingValue: false);
            }
        }
    }

    /// <summary>How long after the last keyboard or mouse input the user still counts as active.</summary>
    public static int IdleSeconds
    {
        get => Read(IdleValue) is int seconds ? Math.Clamp(seconds, MinIdleSeconds, MaxIdleSeconds) : DefaultIdleSeconds;
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            key.SetValue(IdleValue, Math.Clamp(value, MinIdleSeconds, MaxIdleSeconds), RegistryValueKind.DWord);
        }
    }

    /// <summary>A preference that cannot be read is simply not set; the defaults then apply.</summary>
    private static object? Read(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(name);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
