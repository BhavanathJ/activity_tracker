using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;
using ActivityTracker.Core.Data;

namespace ActivityTracker.Service.Jobs;

public class RetentionJob : BackgroundService
{
    private readonly ILogger<RetentionJob> _logger;
    private readonly DatabaseManager _dbManager;

    public RetentionJob(ILogger<RetentionJob> logger, DatabaseManager dbManager)
    {
        _logger = logger;
        _dbManager = dbManager;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("Running Retention Job...");
                RunRetention();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error running Retention Job");
            }

            // Run once a day
            await Task.Delay(TimeSpan.FromDays(1), stoppingToken);
        }
    }

    private void RunRetention()
    {
        var cutoffDate = DateTimeOffset.UtcNow.AddDays(-90);
        
        // Find months fully outside the 90-day window.
        // A month is fully outside if its LAST day is before the cutoffDate.
        // We can just query events older than cutoffDate.
        var cutoffUnix = cutoffDate.ToUnixTimeSeconds();
        
        using var conn = _dbManager.GetConnection();
        using var tx = conn.BeginTransaction();
        
        try
        {
            // 1. Calculate rollups
            // Group by year-month, type, process_or_domain, browser
            // SQLite strftime('%Y-%m', start_time, 'unixepoch')
            
            using var cmdRollup = conn.CreateCommand();
            cmdRollup.Transaction = tx;
            cmdRollup.CommandText = @"
                INSERT INTO monthly_summary (month, category, key, browser, total_seconds)
                SELECT 
                    strftime('%Y-%m', start_time, 'unixepoch') as month,
                    type as category,
                    process_or_domain as key,
                    browser,
                    SUM(COALESCE(end_time, @now) - start_time) as total_seconds
                FROM events
                WHERE start_time < @cutoff
                GROUP BY month, category, key, browser
                ON CONFLICT(month, category, key, browser) DO UPDATE SET
                    total_seconds = total_seconds + excluded.total_seconds;
            ";
            cmdRollup.Parameters.AddWithValue("@cutoff", cutoffUnix);
            cmdRollup.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmdRollup.ExecuteNonQuery();

            // 2. Delete raw rows
            using var cmdDelete = conn.CreateCommand();
            cmdDelete.Transaction = tx;
            cmdDelete.CommandText = "DELETE FROM events WHERE start_time < @cutoff;";
            cmdDelete.Parameters.AddWithValue("@cutoff", cutoffUnix);
            var deletedRows = cmdDelete.ExecuteNonQuery();

            tx.Commit();
            _logger.LogInformation($"Retention Job completed. Summarized and deleted {deletedRows} old events.");
        }
        catch (Exception ex)
        {
            tx.Rollback();
            _logger.LogError(ex, "Retention Job failed during transaction.");
            throw;
        }
    }
}
