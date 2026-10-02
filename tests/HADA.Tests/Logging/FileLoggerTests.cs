using HADA.Core.Logging;
using Microsoft.Extensions.Logging;

namespace HADA.Tests.Logging;

public sealed class FileLoggerTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "hada-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void Entries_are_written_with_level_category_and_exception_and_low_levels_are_skipped()
    {
        var path = Path.Combine(_folder, "logs", "service.log");
        using (var provider = new FileLoggerProvider(new FileLoggerOptions { FilePath = path }))
        {
            var logger = provider.CreateLogger("HADA.Test");
            logger.LogDebug("not written");
            logger.LogWarning("Broker {Host} refused the connection", "10.0.0.5");
            logger.LogError(new InvalidOperationException("boom"), "Sensor failed");
        }

        var text = File.ReadAllText(path);
        Assert.DoesNotContain("not written", text);
        Assert.Contains("[WRN] HADA.Test: Broker 10.0.0.5 refused the connection", text);
        Assert.Contains("[ERR] HADA.Test: Sensor failed", text);
        Assert.Contains("System.InvalidOperationException: boom", text);
    }

    [Fact]
    public void A_full_file_is_rolled_and_only_the_configured_number_of_old_files_is_kept()
    {
        var path = Path.Combine(_folder, "tray.log");
        var options = new FileLoggerOptions { FilePath = path, MaxFileBytes = 200, RetainedFiles = 2 };

        // Each provider writes one batch; a file is rolled when a batch finds it over the limit.
        for (var run = 1; run <= 5; run++)
        {
            using var provider = new FileLoggerProvider(options);
            provider.CreateLogger("HADA.Test").LogInformation("run {Run} {Padding}", run, new string('x', 300));
        }

        Assert.Contains("run 5", File.ReadAllText(path));
        Assert.Contains("run 4", File.ReadAllText(Path.Combine(_folder, "tray.1.log")));
        Assert.Contains("run 3", File.ReadAllText(Path.Combine(_folder, "tray.2.log")));
        Assert.False(File.Exists(Path.Combine(_folder, "tray.3.log")));
    }

    [Fact]
    public void A_file_that_cannot_be_written_does_not_break_logging()
    {
        // A folder where the file should be makes every write fail.
        var path = Path.Combine(_folder, "blocked.log");
        Directory.CreateDirectory(path);

        using var provider = new FileLoggerProvider(new FileLoggerOptions { FilePath = path });
        provider.CreateLogger("HADA.Test").LogError("dropped");
    }
}
