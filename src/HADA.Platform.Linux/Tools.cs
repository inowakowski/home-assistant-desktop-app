using System.ComponentModel;
using System.Diagnostics;

namespace HADA.Platform.Linux;

/// <summary>What a system tool printed, and how it ended.</summary>
public readonly record struct ToolResult(int ExitCode, string Output, string Error)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>Runs the system's own tools, such as <c>loginctl</c> and <c>systemctl</c>, from where the system keeps them.</summary>
public static class Tools
{
    private static readonly string[] Folders = ["/usr/bin", "/bin", "/usr/sbin", "/sbin"];
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>The tool's full path, or <see langword="null"/> where the system has no such tool.</summary>
    public static string? Find(string name) =>
        Folders.Select(folder => Path.Combine(folder, name)).FirstOrDefault(File.Exists);

    /// <summary>Runs a tool and waits for it; <see langword="null"/> where it is missing or could not be started.</summary>
    public static ToolResult? Run(string name, params string[] arguments)
    {
        if (Find(name) is not { } path)
        {
            return null;
        }

        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        // Plain messages, whatever the user's language, as they are read here and not shown.
        start.Environment["LC_ALL"] = "C";

        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(Timeout))
            {
                process.Kill(entireProcessTree: true);
                return new ToolResult(-1, string.Empty, $"{name} did not finish within {Timeout.TotalSeconds:0} seconds.");
            }

            return new ToolResult(process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult().Trim());
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Why a tool's command did not work, in a sentence for the log.</summary>
    public static string Describe(string name, ToolResult? result) => result switch
    {
        null => $"{name} is missing or could not be started.",
        { Error.Length: > 0 } => $"{name} exited with code {result.Value.ExitCode}: {result.Value.Error}",
        _ => $"{name} exited with code {result.Value.ExitCode}.",
    };
}
