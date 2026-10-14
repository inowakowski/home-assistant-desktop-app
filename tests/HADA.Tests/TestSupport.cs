using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using HADA.Service.Settings;

namespace HADA.Tests;

/// <summary>A test of something only Windows has, such as a Windows sensor; skipped on other systems.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Needs Windows.";
        }
    }
}

/// <inheritdoc cref="WindowsFactAttribute"/>
public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Needs Windows.";
        }
    }
}

internal static class TestSetup
{
    /// <summary>
    /// HADA cannot protect secrets on other systems yet, so there the tests that save settings use a stand-in.
    /// On Windows they keep using DPAPI, as the service does.
    /// </summary>
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255:The 'ModuleInitializer' attribute should not be used in libraries", Justification = "Runs once, before any test.")]
    internal static void ProtectSecretsOffWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            SettingsStore.Secrets = new StandInSecretProtector();
        }
    }

    /// <summary>Not encryption: marks a secret as protected so that anything else is rejected, as DPAPI rejects it.</summary>
    private sealed class StandInSecretProtector : ISecretProtector
    {
        private const string Marker = "hada-test:";

        public string Protect(string secret) => Convert.ToBase64String(Encoding.UTF8.GetBytes(Marker + secret));

        public string? TryUnprotect(string? protectedSecret)
        {
            try
            {
                var text = string.IsNullOrEmpty(protectedSecret) ? null : Encoding.UTF8.GetString(Convert.FromBase64String(protectedSecret));
                return text is not null && text.StartsWith(Marker, StringComparison.Ordinal) ? text[Marker.Length..] : null;
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }
}
