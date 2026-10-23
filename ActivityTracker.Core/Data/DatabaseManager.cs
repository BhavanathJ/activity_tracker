using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using ActivityTracker.Core.Models;

namespace ActivityTracker.Core.Data;

public class DatabaseManager
{
    private readonly string _connectionString;
    private readonly bool _isReadOnly;
    private readonly ILogger? _logger;
    private const int SqliteBusyCode = 5;

    public DatabaseManager(ILogger<DatabaseManager>? logger = null)
    {
        _logger = logger;
        _isReadOnly = false;

        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ActivityTracker",
            "tracker.db");
            
        var directory = Path.GetDirectoryName(dbPath);
        if (!Directory.Exists(directory) && directory != null)
        {
            Directory.CreateDirectory(directory);
        }

        var key = KeyManager.GetOrGenerateKey();
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Password = key
        }.ToString();

        InitializeSchema();
    }
    
    // For CLI ReadOnly access
    public DatabaseManager(bool readOnly, ILogger? logger = null)
    {
        _logger = logger;
        _isReadOnly = readOnly;

        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ActivityTracker",
            "tracker.db");

        var key = KeyManager.GetOrGenerateKey();
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Password = key
        }.ToString();
    }

    private SqliteConnection CreateAndOpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA busy_timeout=5000;";
            command.ExecuteNonQuery();

            if (!_isReadOnly)
            {
                command.CommandText = "PRAGMA journal_mode=WAL;";
                command.ExecuteNonQuery();
            }
        }

        return connection;
    }

    private void InitializeSchema()
    {
        using var connection = CreateAndOpenConnection();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                type TEXT NOT NULL,
                process_or_domain TEXT NOT NULL,
                browser TEXT,
                title TEXT NOT NULL,
                start_time INTEGER NOT NULL,
                end_time INTEGER
            );

            CREATE TABLE IF NOT EXISTS monthly_summary (
                month TEXT NOT NULL,
                category TEXT NOT NULL,
                key TEXT NOT NULL,
                browser TEXT NOT NULL DEFAULT '',
                total_seconds INTEGER NOT NULL,
                PRIMARY KEY (month, category, key, browser)
            );

            CREATE INDEX IF NOT EXISTS idx_events_start ON events(start_time);
            CREATE INDEX IF NOT EXISTS idx_events_type_key ON events(type, process_or_domain);
        ";
        command.ExecuteNonQuery();

        // Migrate existing NULLs to '' in monthly_summary
        try
        {
            using var pragmaCmd = connection.CreateCommand();
            pragmaCmd.CommandText = "PRAGMA table_info(monthly_summary);";
            using var reader = pragmaCmd.ExecuteReader();
            bool needsMigration = false;
            while (reader.Read())
            {
                var colName = reader.GetString(1);
                var notNull = reader.GetInt32(3);
                if (string.Equals(colName, "browser", StringComparison.OrdinalIgnoreCase) && notNull == 0)
                {
                    needsMigration = true;
                    break;
                }
            }
            reader.Close();

            if (needsMigration)
            {
                using var migCmd = connection.CreateCommand();
                migCmd.CommandText = @"
                    CREATE TABLE _monthly_summary_new (
                        month TEXT NOT NULL,
                        category TEXT NOT NULL,
                        key TEXT NOT NULL,
                        browser TEXT NOT NULL DEFAULT '',
                        total_seconds INTEGER NOT NULL,
                        PRIMARY KEY (month, category, key, browser)
                    );
                    INSERT OR IGNORE INTO _monthly_summary_new (month, category, key, browser, total_seconds)
                    SELECT month, category, key, COALESCE(browser, ''), SUM(total_seconds)
                    FROM monthly_summary
                    GROUP BY month, category, key, COALESCE(browser, '');

                    DROP TABLE monthly_summary;
                    ALTER TABLE _monthly_summary_new RENAME TO monthly_summary;
                ";
                migCmd.ExecuteNonQuery();
            }
            else
            {
                using var updateCmd = connection.CreateCommand();
                updateCmd.CommandText = "UPDATE monthly_summary SET browser = '' WHERE browser IS NULL;";
                updateCmd.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to migrate monthly_summary schema.");
        }
    }

    public long InsertEvent(EventRecord record)
    {
        using var connection = CreateAndOpenConnection();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO events (type, process_or_domain, browser, title, start_time, end_time)
            VALUES (@type, @process_or_domain, @browser, @title, @start_time, @end_time);
            SELECT last_insert_rowid();
        ";
        command.Parameters.AddWithValue("@type", record.Type);
        command.Parameters.AddWithValue("@process_or_domain", record.ProcessOrDomain);
        command.Parameters.AddWithValue("@browser", (object?)record.Browser ?? DBNull.Value);
        command.Parameters.AddWithValue("@title", record.Title);
        command.Parameters.AddWithValue("@start_time", record.StartTime);
        command.Parameters.AddWithValue("@end_time", (object?)record.EndTime ?? DBNull.Value);

        return (long)command.ExecuteScalar()!;
    }

    public void UpdateEventEndTime(long id, long endTime)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var connection = CreateAndOpenConnection();

                // Fetch start_time to validate before writing
                using var checkCmd = connection.CreateCommand();
                checkCmd.CommandText = "SELECT start_time, process_or_domain FROM events WHERE id = @id;";
                checkCmd.Parameters.AddWithValue("@id", id);

                using var reader = checkCmd.ExecuteReader();
                if (!reader.Read()) return; // row already deleted

                var startTime = reader.GetInt64(0);
                var processOrDomain = reader.GetString(1);
                reader.Close();

                if (endTime < startTime)
                {
                    // Negative duration — CLAMP end = start and log a warning (do not delete).
                    _logger?.LogWarning(
                        "Negative-duration event clamped: id={EventId}, process={Process}, start_time={StartTime}, end_time={EndTime} → clamped to start_time",
                        id, processOrDomain, startTime, endTime);
                    endTime = startTime;
                }

                using var command = connection.CreateCommand();
                // Only update rows whose end_time is still NULL so closed rows are never overwritten.
                command.CommandText = "UPDATE events SET end_time = @end_time WHERE id = @id AND end_time IS NULL;";
                command.Parameters.AddWithValue("@end_time", endTime);
                command.Parameters.AddWithValue("@id", id);
                command.ExecuteNonQuery();

                return;
            }
            catch (SqliteException ex) when (attempt == 0 && IsSqliteBusy(ex))
            {
                _logger?.LogWarning(ex, "Database busy (SQLITE_BUSY) in UpdateEventEndTime for id={EventId}. Retrying once...", id);
                Thread.Sleep(100);
            }
        }
    }

    public int TrimEventsAfter(long backdateUnix)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var connection = CreateAndOpenConnection();
                using var transaction = connection.BeginTransaction();

                int rowsTrimmed = 0;

                using (var command1 = connection.CreateCommand())
                {
                    command1.Transaction = transaction;
                    command1.CommandText = @"
                        UPDATE events
                        SET end_time = @backdateUnix
                        WHERE type IN ('window', 'browser')
                          AND start_time < @backdateUnix
                          AND end_time > @backdateUnix;";
                    command1.Parameters.AddWithValue("@backdateUnix", backdateUnix);
                    rowsTrimmed += command1.ExecuteNonQuery();
                }

                using (var command2 = connection.CreateCommand())
                {
                    command2.Transaction = transaction;
                    command2.CommandText = @"
                        UPDATE events
                        SET end_time = start_time
                        WHERE type IN ('window', 'browser')
                          AND start_time >= @backdateUnix
                          AND end_time IS NOT NULL
                          AND end_time > start_time;";
                    command2.Parameters.AddWithValue("@backdateUnix", backdateUnix);
                    rowsTrimmed += command2.ExecuteNonQuery();
                }

                transaction.Commit();

                _logger?.LogInformation("Trimmed {Count} rows after backdate {BackdateUnix}.", rowsTrimmed, backdateUnix);
                return rowsTrimmed;
            }
            catch (SqliteException ex) when (attempt == 0 && IsSqliteBusy(ex))
            {
                _logger?.LogWarning(ex, "Database busy (SQLITE_BUSY) in TrimEventsAfter for backdateUnix={BackdateUnix}. Retrying once...", backdateUnix);
                Thread.Sleep(100);
            }
        }

        return 0;
    }

    private static bool IsSqliteBusy(SqliteException ex)
    {
        return ex.SqliteErrorCode == SqliteBusyCode || (ex.SqliteExtendedErrorCode & 0xFF) == SqliteBusyCode;
    }
    
    public SqliteConnection GetConnection()
    {
        return CreateAndOpenConnection();
    }
}
