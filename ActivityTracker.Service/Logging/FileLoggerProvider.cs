using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace ActivityTracker.Service.Logging;

public class FileLoggerProvider : ILoggerProvider
{
    private readonly string _filePath;
    private readonly LogLevel _minLevel;
    private readonly long _maxFileSizeBytes;
    private readonly int _maxRetainedFiles;
    private readonly object _lock = new();

    public FileLoggerProvider(
        string filePath, 
        LogLevel minLevel = LogLevel.Information, 
        long maxFileSizeBytes = 10 * 1024 * 1024, 
        int maxRetainedFiles = 5)
    {
        _filePath = filePath;
        _minLevel = minLevel;
        _maxFileSizeBytes = maxFileSizeBytes;
        _maxRetainedFiles = maxRetainedFiles;

        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new FileLogger(categoryName, this);
    }

    public bool IsEnabled(LogLevel logLevel) => logLevel >= _minLevel && logLevel != LogLevel.None;

    public void WriteLog(string categoryName, LogLevel logLevel, string message, Exception? exception)
    {
        if (!IsEnabled(logLevel)) return;

        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var levelStr = logLevel switch
        {
            LogLevel.Trace => "TRCE",
            LogLevel.Debug => "DBUG",
            LogLevel.Information => "INFO",
            LogLevel.Warning => "WARN",
            LogLevel.Error => "FAIL",
            LogLevel.Critical => "CRIT",
            _ => "INFO"
        };

        // Shorten category name for readability if it's a full type name
        var shortCategory = categoryName;
        var lastDot = categoryName.LastIndexOf('.');
        if (lastDot >= 0 && lastDot < categoryName.Length - 1)
        {
            shortCategory = categoryName.Substring(lastDot + 1);
        }

        var logLine = $"[{timestamp}] [{levelStr}] [{shortCategory}] {message}";
        if (exception != null)
        {
            logLine += Environment.NewLine + exception;
        }

        lock (_lock)
        {
            try
            {
                RollOverIfNeeded();

                using var fs = new FileStream(_filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(fs);
                writer.WriteLine(logLine);
            }
            catch
            {
                // Never let logging exceptions crash the service
            }
        }
    }

    private void RollOverIfNeeded()
    {
        try
        {
            var fileInfo = new FileInfo(_filePath);
            if (fileInfo.Exists && fileInfo.Length >= _maxFileSizeBytes)
            {
                var dir = Path.GetDirectoryName(_filePath) ?? "";
                var fileNameWithoutExt = Path.GetFileNameWithoutExtension(_filePath);
                var ext = Path.GetExtension(_filePath);
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var rolledPath = Path.Combine(dir, $"{fileNameWithoutExt}_{timestamp}{ext}");

                File.Move(_filePath, rolledPath, overwrite: true);

                var rolledFiles = new DirectoryInfo(dir)
                    .GetFiles($"{fileNameWithoutExt}_*{ext}")
                    .OrderByDescending(f => f.CreationTime)
                    .Skip(_maxRetainedFiles)
                    .ToList();

                foreach (var oldFile in rolledFiles)
                {
                    try { oldFile.Delete(); } catch { }
                }
            }
        }
        catch
        {
        }
    }

    public void Dispose()
    {
    }
}

public class FileLogger : ILogger
{
    private readonly string _categoryName;
    private readonly FileLoggerProvider _provider;

    public FileLogger(string categoryName, FileLoggerProvider provider)
    {
        _categoryName = categoryName;
        _provider = provider;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => _provider.IsEnabled(logLevel);

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var message = formatter(state, exception);
        if (string.IsNullOrEmpty(message) && exception == null) return;

        _provider.WriteLog(_categoryName, logLevel, message, exception);
    }
}

public static class FileLoggerExtensions
{
    public static ILoggingBuilder AddFile(
        this ILoggingBuilder builder, 
        string filePath, 
        LogLevel minLevel = LogLevel.Information,
        long maxFileSizeBytes = 10 * 1024 * 1024,
        int maxRetainedFiles = 5)
    {
        builder.AddProvider(new FileLoggerProvider(filePath, minLevel, maxFileSizeBytes, maxRetainedFiles));
        return builder;
    }
}
