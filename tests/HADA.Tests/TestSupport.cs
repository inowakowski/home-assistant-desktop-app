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

/// <summary>A test of something only Linux has, such as <c>/proc</c>; skipped elsewhere.</summary>
public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "Needs Linux.";
        }
    }
}

/// <summary>A test of something only macOS has, such as its kernel's statistics; skipped elsewhere.</summary>
public sealed class MacOSFactAttribute : FactAttribute
{
    public MacOSFactAttribute()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Skip = "Needs macOS.";
        }
    }
}

internal static class TestSetup
{
    /// <summary>
    /// Off Windows the service keeps its key in the user's data folder; the tests keep theirs in a folder of their own.
    /// On Windows they keep using DPAPI, as the service does.
    /// </summary>
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255:The 'ModuleInitializer' attribute should not be used in libraries", Justification = "Runs once, before any test.")]
    internal static void ProtectSecretsOffWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            var folder = Path.Combine(Path.GetTempPath(), "hada-tests-" + Guid.NewGuid().ToString("N"));
            SettingsStore.Secrets = new KeyFileSecretProtector(Path.Combine(folder, KeyFileSecretProtector.FileName));
        }
    }
}
