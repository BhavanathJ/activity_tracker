using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Microsoft.Data.Sqlite;
using Spectre.Console;
using Spectre.Console.Cli;
using ActivityTracker.Core.Data;

namespace ActivityTracker.Cli.Commands;

public class ReportCommand : Command<ReportCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandOption("--today")]
        [Description("Show report for today")]
        public bool Today { get; set; }

        [CommandOption("--week")]
        [Description("Show report for the last 7 days")]
        public bool Week { get; set; }

        [CommandOption("--month")]
        [Description("Show report for a specific month (YYYY-MM)")]
        public string? Month { get; set; }

        [CommandOption("--raw")]
        [Description("Dump raw event rows for a given process name (today only, diagnostic)")]
        public string? RawProcess { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        // Diagnostic: dump raw event rows for a process
        if (!string.IsNullOrEmpty(settings.RawProcess))
        {
            return ExecuteRawDump(settings.RawProcess);
        }

        if (!settings.Today && !settings.Week && string.IsNullOrEmpty(settings.Month))
        {
            AnsiConsole.MarkupLine("[red]Please specify a timeframe: --today, --week, or --month YYYY-MM[/]");
            return 1;
        }

        // Initialize db manager in ReadOnly mode to avoid locking issues with the service
        var dbManager = new DatabaseManager(readOnly: true);

        DateTimeOffset start;
        DateTimeOffset end = DateTimeOffset.UtcNow;

        if (settings.Today)
        {
            var localToday = DateTime.Today;
            start = new DateTimeOffset(localToday, DateTimeOffset.Now.Offset);
        }
        else if (settings.Week)
        {
            var localWeekStart = DateTime.Today.AddDays(-7);
            start = new DateTimeOffset(localWeekStart, DateTimeOffset.Now.Offset);
        }
        else
        {
            if (!DateTime.TryParse($"{settings.Month}-01", out var parsedMonth))
            {
                AnsiConsole.MarkupLine("[red]Invalid month format. Use YYYY-MM.[/]");
                return 1;
            }
            start = new DateTimeOffset(parsedMonth, DateTimeOffset.Now.Offset);
            end = start.AddMonths(1);
        }

        var data = new List<ReportRow>();
        if (!string.IsNullOrEmpty(settings.Month))
        {
            var summaryData = FetchSummaryData(dbManager, settings.Month);
            var rawData = FetchRawData(dbManager, start.ToUnixTimeSeconds(), end.ToUnixTimeSeconds());
            
            data = summaryData.Concat(rawData)
                .GroupBy(r => new { r.Category, r.Key, r.Browser })
                .Select(g => new ReportRow
                {
                    Category = g.Key.Category,
                    Key = g.Key.Key,
                    Browser = g.Key.Browser,
                    TotalSeconds = g.Sum(x => x.TotalSeconds)
                })
                .ToList();
        }
        else
        {
            data = FetchRawData(dbManager, start.ToUnixTimeSeconds(), end.ToUnixTimeSeconds());
        }

        RenderReport(data, start, end);

        return 0;
    }

    private int ExecuteRawDump(string processName)
    {
        var dbManager = new DatabaseManager(readOnly: true);
        var todayLocal = DateTime.Today;
        var todayUnix = new DateTimeOffset(todayLocal, DateTimeOffset.Now.Offset).ToUnixTimeSeconds();
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        using var conn = dbManager.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, process_or_domain, title, start_time, end_time
            FROM events
            WHERE process_or_domain = @process AND start_time >= @today
            ORDER BY start_time;
        ";
        cmd.Parameters.AddWithValue("@process", processName);
        cmd.Parameters.AddWithValue("@today", todayUnix);

        using var reader = cmd.ExecuteReader();
        int count = 0;
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            var process = reader.GetString(1);
            var title = reader.GetString(2);
            var startTime = reader.GetInt64(3);
            var endTime = reader.IsDBNull(4) ? (long?)null : reader.GetInt64(4);

            var effectiveEnd = endTime ?? nowUnix;
            var duration = effectiveEnd - startTime;

            var startDto = DateTimeOffset.FromUnixTimeSeconds(startTime).ToLocalTime();
            var endStr = endTime.HasValue
                ? DateTimeOffset.FromUnixTimeSeconds(endTime.Value).ToLocalTime().ToString("HH:mm:ss")
                : "(open)";

            var durationStr = duration < 0 ? $"{duration}s **NEGATIVE**" : $"{duration}s";

            Console.WriteLine(
                $"ID: {id}  Start: {startDto:HH:mm:ss}  End: {endStr}  " +
                $"Duration: {durationStr}  Process: {process}  Title: {title}");
            count++;
        }

        Console.WriteLine($"\n{count} row(s) for '{processName}' today.");
        return 0;
    }

    private List<ReportRow> FetchSummaryData(DatabaseManager dbManager, string month)
    {
        var data = new List<ReportRow>();
        using var conn = dbManager.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT category, key, browser, total_seconds FROM monthly_summary WHERE month = @month;";
        cmd.Parameters.AddWithValue("@month", month);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            data.Add(new ReportRow
            {
                Category = reader.GetString(0),
                Key = reader.GetString(1),
                Browser = reader.IsDBNull(2) ? null : reader.GetString(2),
                TotalSeconds = reader.GetInt64(3)
            });
        }
        return data;
    }

    private List<ReportRow> FetchRawData(DatabaseManager dbManager, long startUnix, long endUnix)
    {
        var data = new List<ReportRow>();
        using var conn = dbManager.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT type, process_or_domain, browser, SUM(COALESCE(end_time, @now) - start_time) 
            FROM events
            WHERE start_time >= @start AND start_time < @end
            GROUP BY type, process_or_domain, browser;
        ";
        cmd.Parameters.AddWithValue("@start", startUnix);
        cmd.Parameters.AddWithValue("@end", endUnix);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            data.Add(new ReportRow
            {
                Category = reader.GetString(0),
                Key = reader.GetString(1),
                Browser = reader.IsDBNull(2) ? null : reader.GetString(2),
                TotalSeconds = reader.GetInt64(3)
            });
        }
        return data;
    }

    private void RenderReport(List<ReportRow> data, DateTimeOffset start, DateTimeOffset end)
    {
        if (data.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No data found for the selected timeframe.[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[dim]Report period: {start.ToLocalTime():yyyy-MM-dd HH:mm:ss} to {end.ToLocalTime():yyyy-MM-dd HH:mm:ss} ({TimeZoneInfo.Local.StandardName})[/]\n");

        RenderCategoryTable(data, "window", "Apps / Window Focus", "cyan");
        RenderCategoryTable(data, "browser", "Browser Domains", "green", showBrowser: true);
        RenderCategoryTable(data, "audio", "Audio Listening", "yellow", showBrowser: true);
        
        var lockedData = data.Where(d => d.Category == "lock").ToList();
        if (lockedData.Any())
        {
            RenderCategoryTable(lockedData, "lock", "Locked Time", "grey50");
        }
    }

    private void RenderCategoryTable(List<ReportRow> data, string category, string title, string color, bool showBrowser = false)
    {
        var categoryData = data.Where(d => d.Category == category).OrderByDescending(d => d.TotalSeconds).ToList();
        if (!categoryData.Any()) return;

        var table = new Table()
            .BorderColor(Color.White)
            .Border(TableBorder.Rounded)
            .Title($"[bold white]{title}[/]");

        table.AddColumn(new TableColumn("[bold white]Application/Domain[/]").LeftAligned());
        table.AddColumn(new TableColumn("[bold white]Duration[/]").RightAligned());

        var grouped = categoryData.GroupBy(d => d.Key).OrderByDescending(g => g.Sum(x => x.TotalSeconds));

        foreach (var group in grouped)
        {
            var totalDuration = TimeSpan.FromSeconds(group.Sum(x => x.TotalSeconds));
            table.AddRow(
                $"[{color}]{group.Key}[/]",
                $"[{color}]{FormatDuration(totalDuration)}[/]"
            );

            if (showBrowser && group.Count() > 1)
            {
                // Has multiple browsers for the same domain
                foreach (var browserRow in group.OrderByDescending(x => x.TotalSeconds))
                {
                    if (string.IsNullOrEmpty(browserRow.Browser)) continue;
                    var bDuration = TimeSpan.FromSeconds(browserRow.TotalSeconds);
                    table.AddRow(
                        $"  [dim {color}]└─ {browserRow.Browser}[/]",
                        $"[dim {color}]{FormatDuration(bDuration)}[/]"
                    );
                }
            }
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        return $"{ts.Minutes}m {ts.Seconds}s";
    }

    private class ReportRow
    {
        public string Category { get; set; } = "";
        public string Key { get; set; } = "";
        public string? Browser { get; set; }
        public long TotalSeconds { get; set; }
    }
}
