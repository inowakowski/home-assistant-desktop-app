using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text;
using HADA.Service.Settings;
using Microsoft.Win32;

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

/// <summary>
/// A test that changes a setting of Windows, skipped on Windows Server. The hosted build agents are Windows Server
/// 2025, where setting the quiet hours ("Do not disturb") crashes the service behind them (QuietHours.dll in
/// WpnUserService, exception 0xC000027B) every time; it restarts, and the calls of whoever asks in the meantime fail.
/// </summary>
public sealed class WindowsClientFactAttribute : FactAttribute
{
    public WindowsClientFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Needs Windows.";
        }
        else if (IsWindowsServer())
        {
            Skip = "Needs a Windows that is not a Server.";
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsWindowsServer()
    {
        // "WinNT" on a workstation; "ServerNT" or "LanmanNT" (a domain controller) on a server.
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\ProductOptions");
        return key?.GetValue("ProductType") is string productType && !string.Equals(productType, "WinNT", StringComparison.OrdinalIgnoreCase);
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
