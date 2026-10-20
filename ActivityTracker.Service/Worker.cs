using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ActivityTracker.Core.Data;
using ActivityTracker.Service.Tracking;
using ActivityTracker.Service.Http;

namespace ActivityTracker.Service;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly DatabaseManager _dbManager;
    private WindowSessionManager? _windowSessionManager;
    private ExtensionListener? _httpListener;
    private SystemStateTracker? _systemStateTracker;

    public Worker(ILogger<Worker> logger, DatabaseManager dbManager)
    {
        _logger = logger;
        _dbManager = dbManager;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Activity Tracker Service starting.");

        // Ensure the meta table exists
        EnsureMetaTable();

        // Fix orphaned rows left open by a previous crash.
        FixOrphanedEvents();

        _windowSessionManager = new WindowSessionManager(_logger, _dbManager);
        _httpListener = new ExtensionListener(_logger, _dbManager, _windowSessionManager);

        // SystemStateTracker needs to hook into power/session events.
        // In a true Windows Service, we'd override OnSessionChange/OnPowerEvent in ServiceBase.
        // For BackgroundService we might need to use SystemEvents (which requires a message pump)
        // or WTSRegisterSessionNotification. We'll implement this carefully in SystemStateTracker.
        _systemStateTracker = new SystemStateTracker(_logger, _windowSessionManager, _dbManager, _httpListener);

        _httpListener.Start();
        _systemStateTracker.Start();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // Write last_seen heartbeat every 30 s
                WriteLastSeen();
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
        catch (TaskCanceledException)
        {
            // Expected
        }
        finally
        {
            _systemStateTracker.Stop();
            _httpListener.Stop();

            // Close everything before stopping
            _httpListener.CloseEverything(DateTimeOffset.UtcNow);

            _logger.LogInformation("Activity Tracker Service stopped.");
        }
    }

    private void EnsureMetaTable()
    {
        try
        {
            using var conn = _dbManager.GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value INTEGER);";
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create meta table.");
        }
    }

    private void WriteLastSeen()
    {
        try
        {
            var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using var conn = _dbManager.GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO meta (key, value) VALUES ('last_seen', @v) ON CONFLICT(key) DO UPDATE SET value = @v;";
            cmd.Parameters.AddWithValue("@v", nowUnix);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write last_seen.");
        }
    }

    private void FixOrphanedEvents()
    {
        try
        {
            var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // Read last_seen from meta table (if it exists)
            long? lastSeen = null;
            using (var conn = _dbManager.GetConnection())
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT value FROM meta WHERE key = 'last_seen';";
                var result = cmd.ExecuteScalar();
                if (result != null && result != DBNull.Value)
                {
                    lastSeen = Convert.ToInt64(result);
                }
            }

            using (var conn = _dbManager.GetConnection())
            {
                using var cmd = conn.CreateCommand();
                if (lastSeen.HasValue)
                {
                    // end_time = MAX(start_time, MIN(last_seen, now))
                    var cappedLastSeen = Math.Min(lastSeen.Value, nowUnix);
                    cmd.CommandText = @"
                        UPDATE events SET end_time = MAX(start_time, @capped_last_seen) 
                        WHERE end_time IS NULL;";
                    cmd.Parameters.AddWithValue("@capped_last_seen", cappedLastSeen);
                }
                else
                {
                    // No last_seen — fall back to start_time
                    cmd.CommandText = "UPDATE events SET end_time = start_time WHERE end_time IS NULL;";
                }

                var affected = cmd.ExecuteNonQuery();
                if (affected > 0)
                    _logger.LogWarning("Fixed {Count} orphaned event(s) with NULL end_time.", affected);
                else
                    _logger.LogInformation("No orphaned events found.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fix orphaned events.");
        }
    }
}
