namespace HADA.Platform.Linux;

/// <summary>
/// Whether Linux waits for a restart to finish an update. There is no one way to tell on every distribution, so
/// this looks where the common ones say so.
/// </summary>
public static class PendingRestart
{
    /// <summary>
    /// <c>packages</c> where Debian and Ubuntu flag that packages want a restart, <c>kernel</c> where the kernel
    /// running is no longer installed, both joined by a comma, or empty when neither. <see langword="null"/>
    /// where neither can be told, and the sensor is left out.
    /// </summary>
    public static string? Read(string root = "/")
    {
        var flagFolder = Path.Combine(root, "var", "run");
        var modules = Path.Combine(root, "usr", "lib", "modules");
        var release = Proc.ReadText(Path.Combine(root, "proc", "sys", "kernel", "osrelease"))?.Trim();

        var debian = File.Exists(Path.Combine(root, "etc", "debian_version"));
        var kernelKnown = release is { Length: > 0 } && Directory.Exists(modules);
        if (!debian && !kernelKnown)
        {
            return null;
        }

        var reasons = new List<string>(2);
        if (File.Exists(Path.Combine(flagFolder, "reboot-required")))
        {
            reasons.Add("packages");
        }

        // An update that replaces the kernel removes the modules of the one running.
        if (kernelKnown && !Directory.Exists(Path.Combine(modules, release!)))
        {
            reasons.Add("kernel");
        }

        return string.Join(',', reasons);
    }
}
