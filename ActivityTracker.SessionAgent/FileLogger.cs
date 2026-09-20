using System;
using System.IO;

namespace ActivityTracker.SessionAgent;

public static class FileLogger
{
    private static readonly object _lock = new();
    private static readonly string _logPath;
    private const long MaxFileSizeBytes = 5 * 1024 * 1024; // 5 MB

    static FileLogger()
    {
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ActivityTracker",
            "logs");

        _logPath = Path.Combine(logDir, "sessionagent.log");

        try
        {
            if (!Directory.Exists(logDir))
            {
                Directory.CreateDirectory(logDir);
            }
        }
        catch
        {
            // Directory creation failure will be handled gracefully during write
        }
    }

    public static void LogInfo(string message) => WriteLog("INFO", message);
    public static void LogWarn(string message) => WriteLog("WARN", message);
    public static void LogError(string message, Exception? ex = null)
    {
        var msg = ex != null ? $"{message}: {ex}" : message;
        WriteLog("FAIL", msg);
    }

    private static void WriteLog(string level, string message)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var logLine = $"[{timestamp}] [{level}] [SessionAgent] {message}";

        // Write to Console.Error in case stderr is redirected or attached to console
        try
        {
            Console.Error.WriteLine(logLine);
        }
        catch
        {
        }

        lock (_lock)
        {
            try
            {
                RollOverIfNeeded();

                using var fs = new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(fs);
                writer.WriteLine(logLine);
            }
            catch
            {
                // Never let logging exceptions crash SessionAgent
            }
        }
    }

    private static void RollOverIfNeeded()
    {
        try
        {
            var fileInfo = new FileInfo(_logPath);
            if (fileInfo.Exists && fileInfo.Length >= MaxFileSizeBytes)
            {
                var dir = Path.GetDirectoryName(_logPath) ?? string.Empty;
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var rolledPath = Path.Combine(dir, $"sessionagent_{timestamp}.log");

                File.Move(_logPath, rolledPath, overwrite: true);
            }
        }
        catch
        {
        }
    }
}
