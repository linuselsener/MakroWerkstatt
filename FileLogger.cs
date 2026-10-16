using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;

namespace MakroWerkstatt;

public static class FileLogger
{
    private const int QueueCapacity = 4096;
    private const long RateLimitWindowMs = 1000;
    private static readonly object StateLock = new();
    private static readonly object RateLimitLock = new();
    private static readonly Dictionary<string, RateLimitState> RateLimits = new(StringComparer.Ordinal);
    private static BlockingCollection<LogEntry>? _queue;
    private static Thread? _worker;
    private static string? _logDirectory;
    private static Func<DateTimeOffset> _timestampProvider = static () => DateTimeOffset.Now;

    public static void Initialize(string? directoryOverride = null) =>
        InitializeCore(directoryOverride ?? GetConfiguredLogDirectory(), static () => DateTimeOffset.Now);

    private static string? GetConfiguredLogDirectory()
    {
        var configuredDataDirectory = Environment.GetEnvironmentVariable("MAKROWERKSTATT_DATA_DIR");
        return string.IsNullOrWhiteSpace(configuredDataDirectory)
            ? null
            : Path.Combine(configuredDataDirectory, "logs");
    }

    internal static void InitializeForTesting(string directoryOverride, Func<DateTimeOffset> timestampProvider)
    {
        ArgumentNullException.ThrowIfNull(timestampProvider);
        InitializeCore(directoryOverride, timestampProvider);
    }

    private static void InitializeCore(string? directoryOverride, Func<DateTimeOffset> timestampProvider)
    {
        lock (StateLock)
        {
            if (_queue is not null) return;
            try
            {
                var logDirectory = directoryOverride ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MakroWerkstatt", "logs");
                Directory.CreateDirectory(logDirectory);
                RemoveExpiredLogs(logDirectory);
                var queue = new BlockingCollection<LogEntry>(new ConcurrentQueue<LogEntry>(), QueueCapacity);
                Volatile.Write(ref _logDirectory, logDirectory);
                Volatile.Write(ref _timestampProvider, timestampProvider);
                Volatile.Write(ref _queue, queue);
                _worker = new Thread(() => WriteEntries(queue))
                {
                    IsBackground = true,
                    Name = "MakroWerkstatt-FileLogger"
                };
                _worker.Start();
            }
            catch
            {
                _queue?.Dispose();
                Volatile.Write(ref _queue, (BlockingCollection<LogEntry>?)null);
                _worker = null;
                Volatile.Write(ref _logDirectory, (string?)null);
                Volatile.Write(ref _timestampProvider, (Func<DateTimeOffset>)(static () => DateTimeOffset.Now));
            }
        }
    }

    public static void Info(string source, string message) => Enqueue(LogLevel.Info, source, message, null, null, rateLimit: false);

    public static void Warning(string source, string message, Exception? exception = null, int? win32ErrorCode = null) =>
        Enqueue(LogLevel.Warn, source, message, exception, win32ErrorCode, rateLimit: true);

    public static void Error(string source, string message, Exception? exception = null, int? win32ErrorCode = null) =>
        Enqueue(LogLevel.Error, source, message, exception, win32ErrorCode, rateLimit: true);

    public static void Shutdown()
    {
        BlockingCollection<LogEntry>? queue;
        Thread? worker;
        lock (StateLock)
        {
            queue = Volatile.Read(ref _queue);
            worker = _worker;
            Volatile.Write(ref _queue, (BlockingCollection<LogEntry>?)null);
            _worker = null;
            Volatile.Write(ref _logDirectory, (string?)null);
            Volatile.Write(ref _timestampProvider, (Func<DateTimeOffset>)(static () => DateTimeOffset.Now));
        }

        if (queue is null) return;
        try { queue.CompleteAdding(); } catch { }
        try { worker?.Join(TimeSpan.FromSeconds(2)); } catch { }
        try { queue.Dispose(); } catch { }
    }

    private static void Enqueue(LogLevel level, string source, string message, Exception? exception,
        int? win32ErrorCode, bool rateLimit)
    {
        try
        {
            var queue = Volatile.Read(ref _queue);
            var directory = Volatile.Read(ref _logDirectory);
            if (queue is null || directory is null || queue.IsAddingCompleted) return;
            var timestamp = Volatile.Read(ref _timestampProvider)();
            var suppressed = rateLimit ? GetSuppressedCount(level, source, message, exception, win32ErrorCode, timestamp) : 0;
            if (suppressed < 0) return;
            queue.TryAdd(new LogEntry(timestamp, directory, level, source, message, exception, win32ErrorCode, suppressed));
        }
        catch
        {
            // Logging is best effort and must never affect input handling.
        }
    }

    private static int GetSuppressedCount(LogLevel level, string source, string message, Exception? exception,
        int? win32ErrorCode, DateTimeOffset timestamp)
    {
        var key = string.Join("|", level, source, message, exception?.GetType().FullName ?? "",
            exception?.HResult.ToString(CultureInfo.InvariantCulture) ?? "", win32ErrorCode?.ToString(CultureInfo.InvariantCulture) ?? "");
        var now = Environment.TickCount64;
        lock (RateLimitLock)
        {
            if (!RateLimits.TryGetValue(key, out var state))
            {
                RateLimits[key] = new RateLimitState(now, 0);
                return 0;
            }
            if (now - state.LastLoggedTick < RateLimitWindowMs)
            {
                RateLimits[key] = state with { SuppressedCount = state.SuppressedCount + 1 };
                return -1;
            }
            RateLimits[key] = new RateLimitState(now, 0);
            return state.SuppressedCount;
        }
    }

    private static void WriteEntries(BlockingCollection<LogEntry> queue)
    {
        try
        {
            foreach (var entry in queue.GetConsumingEnumerable())
            {
                try
                {
                    var date = entry.Timestamp.ToLocalTime().ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                    var path = Path.Combine(entry.Directory, $"makrowerkstatt-{date}.log");
                    File.AppendAllText(path, FormatEntry(entry), new UTF8Encoding(false));
                }
                catch
                {
                    // A locked or unavailable log file must not stop the worker.
                }
            }
        }
        catch
        {
            // Shutdown and collection failures are intentionally contained.
        }
    }

    private static string FormatEntry(LogEntry entry)
    {
        var builder = new StringBuilder()
            .Append(entry.Timestamp.ToString("O", CultureInfo.InvariantCulture))
            .Append(" | ").Append(entry.Level)
            .Append(" | ").Append(entry.Source)
            .Append(" | ").Append(entry.Message);
        if (entry.SuppressedCount > 0)
            builder.Append(" ( ").Append(entry.SuppressedCount).Append(" weitere unterdrückt )");
        if (entry.Exception is { } exception)
        {
            builder.Append(" | ExceptionType=").Append(exception.GetType().Name)
                .Append(" HResult=0x").Append(unchecked((uint)exception.HResult).ToString("X8", CultureInfo.InvariantCulture));
            if (entry.Win32ErrorCode is { } errorCode)
                builder.Append(" Win32Error=").Append(errorCode.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(exception.StackTrace))
                builder.AppendLine().Append("StackTrace: ").Append(exception.StackTrace);
        }
        else if (entry.Win32ErrorCode is { } errorCode)
        {
            builder.Append(" | Win32Error=").Append(errorCode.ToString(CultureInfo.InvariantCulture));
        }
        return builder.AppendLine().ToString();
    }

    private static void RemoveExpiredLogs(string directory)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-14);
            foreach (var path in Directory.EnumerateFiles(directory, "makrowerkstatt-*.log", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoff) File.Delete(path);
                }
                catch { }
            }
        }
        catch { }
    }

    private enum LogLevel { Info, Warn, Error }
    private sealed record LogEntry(DateTimeOffset Timestamp, string Directory, LogLevel Level, string Source, string Message,
        Exception? Exception, int? Win32ErrorCode, int SuppressedCount);
    private sealed record RateLimitState(long LastLoggedTick, int SuppressedCount);
}
