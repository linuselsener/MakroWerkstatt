using MakroWerkstatt;
using Xunit;

namespace MakroWerkstatt.Tests;

[Collection("SerialTests")]
public sealed class FileLoggerTests
{
    [Fact]
    public void Unavailable_log_path_does_not_throw()
    {
        using var context = new TestContext();
        var blockedPath = Path.Combine(context.RootPath, "not-a-directory");
        File.WriteAllText(blockedPath, "synthetic blocker");

        try
        {
            FileLogger.InitializeForTesting(blockedPath, static () => DateTimeOffset.UtcNow);
            FileLogger.Error("test.source", "synthetic failure", new InvalidOperationException("synthetic exception"));
        }
        finally
        {
            FileLogger.Shutdown();
        }
    }

    [Fact]
    public void Identical_errors_are_throttled_and_exception_contents_are_not_logged()
    {
        using var context = new TestContext();
        var logDirectory = Path.Combine(context.RootPath, "logs");
        Directory.CreateDirectory(logDirectory);
        var source = "synthetic-source-" + Guid.NewGuid().ToString("N");
        const string message = "Synthetic callback failed";
        const string privatePayload = "fabricated private payload";

        try
        {
            FileLogger.InitializeForTesting(logDirectory, static () => DateTimeOffset.UtcNow);
            FileLogger.Error(source, message, new InvalidOperationException(privatePayload));
            FileLogger.Error(source, message, new InvalidOperationException(privatePayload));
        }
        finally
        {
            FileLogger.Shutdown();
        }

        var logText = File.ReadAllText(Assert.Single(Directory.GetFiles(logDirectory, "makrowerkstatt-*.log")));
        Assert.Equal(1, CountOccurrences(logText, message));
        Assert.Contains("ExceptionType=InvalidOperationException", logText, StringComparison.Ordinal);
        Assert.DoesNotContain(privatePayload, logText, StringComparison.Ordinal);
    }

    [Fact]
    public void Daily_log_rotation_uses_a_separate_file_per_day()
    {
        using var context = new TestContext();
        var logDirectory = Path.Combine(context.RootPath, "logs");
        Directory.CreateDirectory(logDirectory);
        var currentTime = new DateTimeOffset(2025, 4, 5, 12, 0, 0, TimeSpan.Zero);

        try
        {
            FileLogger.InitializeForTesting(logDirectory, () => currentTime);
            FileLogger.Info("rotation-test", "synthetic day one entry");
            currentTime = currentTime.AddDays(1);
            FileLogger.Info("rotation-test", "synthetic day two entry");
        }
        finally
        {
            FileLogger.Shutdown();
        }

        var logFiles = Directory.GetFiles(logDirectory, "makrowerkstatt-*.log");
        Assert.Equal(2, logFiles.Length);
        var allText = string.Join(Environment.NewLine, logFiles.Select(File.ReadAllText));
        Assert.Contains("synthetic day one entry", allText, StringComparison.Ordinal);
        Assert.Contains("synthetic day two entry", allText, StringComparison.Ordinal);
    }

    [Fact]
    public void Initialize_deletes_logs_older_than_retention_period()
    {
        using var context = new TestContext();
        var logDirectory = Path.Combine(context.RootPath, "logs");
        Directory.CreateDirectory(logDirectory);
        var expiredLog = Path.Combine(logDirectory, "makrowerkstatt-20000101.log");
        File.WriteAllText(expiredLog, "synthetic old log");
        File.SetLastWriteTimeUtc(expiredLog, DateTime.UtcNow.AddDays(-16));

        try
        {
            FileLogger.InitializeForTesting(logDirectory, static () => DateTimeOffset.UtcNow);
        }
        finally
        {
            FileLogger.Shutdown();
        }

        Assert.False(File.Exists(expiredLog));
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
    }
}
