using System.Diagnostics;
using HADA.Core.Abstractions;
using HADA.Core.Hosting;
using HADA.Core.Models;
using Microsoft.Extensions.Logging;

namespace HADA.Core.Actions;

/// <summary>
/// Starts programs for the service in the user's session: the service itself runs where nothing it starts could
/// be seen. Runs in the tray app and has no entity of its own; the buttons belong to the service, which sends
/// <see cref="SessionCommands.Launch"/> with what the administrator configured for the button that was pressed.
/// </summary>
public sealed partial class LaunchAction(IEventBus bus, ILogger<LaunchAction> logger) : EagerBackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var commands = bus.Subscribe<ActionCommand>();
        try
        {
            await foreach (var command in commands.ReadAllAsync(stoppingToken))
            {
                if (command.ActionId != SessionCommands.Launch || string.IsNullOrWhiteSpace(command.Value))
                {
                    continue;
                }

                var (file, arguments) = Split(command.Value);
                try
                {
                    // Through the shell, so documents, folders and addresses such as https://… work as well.
                    Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = true })?.Dispose();
                    LogStarted(logger, file);
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
                {
                    LogFailed(logger, file, ex.Message);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Splits a command line into what to start and its arguments: <c>"C:\Program Files\App\app.exe" --flag</c>,
    /// <c>notepad notes.txt</c> or just <c>https://example.com</c>. A path containing spaces must be quoted, unless
    /// it is all there is.
    /// </summary>
    public static (string File, string Arguments) Split(string commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);

        var text = commandLine.Trim();
        if (text.StartsWith('"'))
        {
            var closing = text.IndexOf('"', 1);
            return closing < 0 ? (text[1..], string.Empty) : (text[1..closing], text[(closing + 1)..].TrimStart());
        }

        var space = text.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 || File.Exists(text) || Directory.Exists(text)
            ? (text, string.Empty)
            : (text[..space], text[(space + 1)..].TrimStart());
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Started '{File}' for the service.")]
    private static partial void LogStarted(ILogger logger, string file);

    [LoggerMessage(Level = LogLevel.Error, Message = "Starting '{File}' failed: {Reason}")]
    private static partial void LogFailed(ILogger logger, string file, string reason);
}
