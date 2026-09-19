using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using ActivityTracker.Core.Models;

namespace ActivityTracker.Core.Data;

public class DatabaseManager
{
    private readonly string _connectionString;

    public DatabaseManager()
    {
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
    public DatabaseManager(bool readOnly)
    {
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

    private void InitializeSchema()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

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
                browser TEXT,
                total_seconds INTEGER NOT NULL,
                PRIMARY KEY (month, category, key, browser)
            );
        ";
        command.ExecuteNonQuery();
    }

    public long InsertEvent(EventRecord record)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

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
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

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
            // Negative duration — discard the row rather than corrupt report data.
            // The warning is deliberately noisy so regressions surface in logs.
            Console.Error.WriteLine(
                $"[WARN] Negative-duration event discarded: id={id}, " +
                $"process={processOrDomain}, start_time={startTime}, end_time={endTime}");

            using var delCmd = connection.CreateCommand();
            delCmd.CommandText = "DELETE FROM events WHERE id = @id;";
            delCmd.Parameters.AddWithValue("@id", id);
            delCmd.ExecuteNonQuery();
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE events SET end_time = @end_time WHERE id = @id;";
        command.Parameters.AddWithValue("@end_time", endTime);
        command.Parameters.AddWithValue("@id", id);
        command.ExecuteNonQuery();
    }
    
    public SqliteConnection GetConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }
}
