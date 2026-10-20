using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
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
        [Description("Dump raw event rows for a given process name (diagnostic)")]
        public string? RawProcess { get; set; }

        [CommandOption("--timeline")]
        [Description("Show timeline of all event types for a time range")]
        public bool Timeline { get; set; }

        [CommandOption("--from")]
        [Description("Start time for timeline (HH:mm)")]
        public string? FromTime { get; set; }

        [CommandOption("--to")]
        [Description("End time for timeline (HH:mm)")]
        public string? ToTime { get; set; }

        [CommandOption("--date")]
        [Description("Date for raw dump or timeline (YYYY-MM-DD, defaults to today)")]
        public string? Date { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        // 5c: Diagnostic: dump raw event rows for a process on a given day (or today)
        if (!string.IsNullOrEmpty(settings.RawProcess))
        {
            return ExecuteRawDump(settings.RawProcess, settings.Date);
        }

        // 5a: Timeline for local-time range
        if (settings.Timeline)
        {
            return ExecuteTimeline(settings.FromTime, settings.ToTime, settings.Date);
        }

        if (!settings.Today && !settings.Week && string.IsNullOrEmpty(settings.Month))
        {
            AnsiConsole.MarkupLine("[red]Please specify a timeframe: --today, --week, --month YYYY-MM, --timeline, or --raw[/]");
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
            if (!DateTime.TryParseExact($"{settings.Month}-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedMonth))
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

        // 5b: After --today and --week, print Data quality footer
        if (settings.Today || settings.Week)
        {
            RenderDataQualityFooter(dbManager, data, start.ToUnixTimeSeconds(), end.ToUnixTimeSeconds());
        }

        return 0;
    }

    private int ExecuteTimeline(string? fromStr, string? toStr, string? dateStr)
    {
        if (string.IsNullOrEmpty(fromStr) || string.IsNullOrEmpty(toStr))
        {
            AnsiConsole.MarkupLine("[red]Error: --timeline requires both --from HH:mm and --to HH:mm[/]");
            return 1;
        }

        DateTime baseDate = DateTime.Today;
        if (!string.IsNullOrEmpty(dateStr))
        {
            if (!DateTime.TryParseExact(dateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out baseDate))
            {
                AnsiConsole.MarkupLine("[red]Invalid date format for --date. Use YYYY-MM-DD.[/]");
                return 1;
            }
        }

        if (!TimeSpan.TryParseExact(fromStr, @"h\:mm", CultureInfo.InvariantCulture, out var fromSpan) &&
            !TimeSpan.TryParseExact(fromStr, @"hh\:mm", CultureInfo.InvariantCulture, out fromSpan) &&
            !TimeSpan.TryParse(fromStr, out fromSpan))
        {
            AnsiConsole.MarkupLine("[red]Invalid --from time format. Use HH:mm (e.g. 09:30).[/]");
            return 1;
        }

        if (!TimeSpan.TryParseExact(toStr, @"h\:mm", CultureInfo.InvariantCulture, out var toSpan) &&
            !TimeSpan.TryParseExact(toStr, @"hh\:mm", CultureInfo.InvariantCulture, out toSpan) &&
            !TimeSpan.TryParse(toStr, out toSpan))
        {
            AnsiConsole.MarkupLine("[red]Invalid --to time format. Use HH:mm (e.g. 17:00).[/]");
            return 1;
        }

        var startDto = new DateTimeOffset(baseDate.Date.Add(fromSpan), DateTimeOffset.Now.Offset);
        var endDto = new DateTimeOffset(baseDate.Date.Add(toSpan), DateTimeOffset.Now.Offset);
        if (endDto < startDto)
        {
            // If end is before start, assume next day
            endDto = endDto.AddDays(1);
        }

        var startUnix = startDto.ToUnixTimeSeconds();
        var endUnix = endDto.ToUnixTimeSeconds();
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var dbManager = new DatabaseManager(readOnly: true);
        using var conn = dbManager.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, type, process_or_domain, browser, title, start_time, end_time
            FROM events
            WHERE (start_time >= @start AND start_time < @end)
               OR (start_time < @start AND (end_time IS NULL OR end_time > @start))
            ORDER BY start_time ASC;
        ";
        cmd.Parameters.AddWithValue("@start", startUnix);
        cmd.Parameters.AddWithValue("@end", endUnix);

        using var reader = cmd.ExecuteReader();
        var eventsList = new List<(long Id, string Type, string ProcessOrDomain, string? Browser, string Title, long StartTime, long? EndTime)>();

        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            var type = reader.GetString(1);
            var process = reader.GetString(2);
            var browser = reader.IsDBNull(3) ? null : reader.GetString(3);
            var title = reader.GetString(4);
            var startTime = reader.GetInt64(5);
            var endTime = reader.IsDBNull(6) ? (long?)null : reader.GetInt64(6);

            eventsList.Add((id, type, process, browser, title, startTime, endTime));
        }

        if (eventsList.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No events found in the specified timeline range.[/]");
            return 0;
        }

        var table = new Table()
            .BorderColor(Color.White)
            .Border(TableBorder.Rounded)
            .Title($"[bold white]Timeline: {startDto:yyyy-MM-dd HH:mm} to {endDto:HH:mm} ({TimeZoneInfo.Local.StandardName})[/]");

        table.AddColumn(new TableColumn("[bold white]Type[/]").LeftAligned());
        table.AddColumn(new TableColumn("[bold white]Start[/]").LeftAligned());
        table.AddColumn(new TableColumn("[bold white]End[/]").LeftAligned());
        table.AddColumn(new TableColumn("[bold white]Duration[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold white]App / Domain[/]").LeftAligned());
        table.AddColumn(new TableColumn("[bold white]Title[/]").LeftAligned());

        foreach (var ev in eventsList)
        {
            var effectiveEnd = ev.EndTime.HasValue ? ev.EndTime.Value : Math.Min(nowUnix, ev.StartTime + 300);
            var duration = effectiveEnd - ev.StartTime;

            var startStr = DateTimeOffset.FromUnixTimeSeconds(ev.StartTime).ToLocalTime().ToString("HH:mm:ss");
            var endStr = ev.EndTime.HasValue
                ? DateTimeOffset.FromUnixTimeSeconds(ev.EndTime.Value).ToLocalTime().ToString("HH:mm:ss")
                : "[italic grey](open)[/]";

            string typeColor = ev.Type switch
            {
                "window" => "cyan",
                "browser" => "green",
                "audio" => "yellow",
                "lock" => "grey50",
                _ => "white"
            };

            var appStr = !string.IsNullOrEmpty(ev.Browser)
                ? $"{ev.ProcessOrDomain} [dim]({ev.Browser})[/]"
                : ev.ProcessOrDomain;

            var durationStr = duration < 0
                ? $"[red]{duration}s[/]"
                : FormatDuration(TimeSpan.FromSeconds(duration));

            table.AddRow(
                $"[{typeColor}]{ev.Type}[/]",
                startStr,
                endStr,
                durationStr,
                $"[{typeColor}]{Markup.Escape(appStr)}[/]",
                Markup.Escape(ev.Title)
            );
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
        return 0;
    }

    private void RenderDataQualityFooter(DatabaseManager dbManager, List<ReportRow> data, long startUnix, long endUnix)
    {
        long openCount = 0;
        var longRows = new List<(long Id, string ProcessOrDomain, long Duration)>();
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        using (var conn = dbManager.GetConnection())
        {
            // 1. Open rows
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM events WHERE start_time >= @start AND start_time < @end AND end_time IS NULL;";
                cmd.Parameters.AddWithValue("@start", startUnix);
                cmd.Parameters.AddWithValue("@end", endUnix);
                openCount = (long)(cmd.ExecuteScalar() ?? 0L);
            }

            // 2. Single rows longer than 2 hours (7200 seconds)
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT id, process_or_domain, (COALESCE(end_time, @now) - start_time) AS dur
                    FROM events
                    WHERE start_time >= @start AND start_time < @end
                      AND (COALESCE(end_time, @now) - start_time) > 7200
                    ORDER BY id;
                ";
                cmd.Parameters.AddWithValue("@start", startUnix);
                cmd.Parameters.AddWithValue("@end", endUnix);
                cmd.Parameters.AddWithValue("@now", nowUnix);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    longRows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2)));
                }
            }
        }

        // 3. Browser-domain total vs browser-process total
        long browserDomainTotal = data.Where(d => d.Category == "browser").Sum(d => d.TotalSeconds);
        long browserProcessTotal = data
            .Where(d => d.Category == "window" &&
                        (d.Key.Equals("brave", StringComparison.OrdinalIgnoreCase) ||
                         d.Key.Equals("chrome", StringComparison.OrdinalIgnoreCase) ||
                         d.Key.Equals("msedge", StringComparison.OrdinalIgnoreCase)))
            .Sum(d => d.TotalSeconds);

        AnsiConsole.MarkupLine("[bold white]Data Quality:[/] ");
        AnsiConsole.MarkupLine($"  Open rows: [white]{openCount}[/]");

        if (longRows.Count > 0)
        {
            var details = string.Join(", ", longRows.Select(r => $"#{r.Id} ({r.ProcessOrDomain})"));
            AnsiConsole.MarkupLine($"  Rows > 2h: [yellow]{longRows.Count}[/] ({details})");
        }
        else
        {
            AnsiConsole.MarkupLine("  Rows > 2h: [white]0[/]");
        }

        if (browserDomainTotal > 0 && browserDomainTotal > browserProcessTotal * 1.05)
        {
            double diffPercent = browserProcessTotal > 0
                ? ((double)(browserDomainTotal - browserProcessTotal) / browserProcessTotal) * 100.0
                : 100.0;
            AnsiConsole.MarkupLine(
                $"  [bold yellow]WARNING:[/] Browser domain total ({FormatDuration(TimeSpan.FromSeconds(browserDomainTotal))}) " +
                $"exceeds browser process focus time ({FormatDuration(TimeSpan.FromSeconds(browserProcessTotal))}) by {diffPercent:F1}%.");
        }

        AnsiConsole.WriteLine();
    }

    private int ExecuteRawDump(string processName, string? dateStr)
    {
        var dbManager = new DatabaseManager(readOnly: true);
        DateTime targetDate = DateTime.Today;

        if (!string.IsNullOrEmpty(dateStr))
        {
            if (!DateTime.TryParseExact(dateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out targetDate))
            {
                AnsiConsole.MarkupLine("[red]Invalid date format for --date. Use YYYY-MM-DD.[/]");
                return 1;
            }
        }

        var startUnix = new DateTimeOffset(targetDate, DateTimeOffset.Now.Offset).ToUnixTimeSeconds();
        var endUnix = new DateTimeOffset(targetDate.AddDays(1), DateTimeOffset.Now.Offset).ToUnixTimeSeconds();
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        using var conn = dbManager.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, process_or_domain, title, start_time, end_time
            FROM events
            WHERE process_or_domain = @process AND start_time >= @start AND start_time < @end
            ORDER BY start_time;
        ";
        cmd.Parameters.AddWithValue("@process", processName);
        cmd.Parameters.AddWithValue("@start", startUnix);
        cmd.Parameters.AddWithValue("@end", endUnix);

        using var reader = cmd.ExecuteReader();
        int count = 0;
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            var process = reader.GetString(1);
            var title = reader.GetString(2);
            var startTime = reader.GetInt64(3);
            var endTime = reader.IsDBNull(4) ? (long?)null : reader.GetInt64(4);

            var effectiveEnd = endTime.HasValue ? endTime.Value : Math.Min(nowUnix, startTime + 300);
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

        Console.WriteLine($"\n{count} row(s) for '{processName}' on {targetDate:yyyy-MM-dd}.");
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
                Browser = reader.IsDBNull(2) ? "" : reader.GetString(2),
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
            SELECT type, process_or_domain, browser,
                   SUM(CASE WHEN end_time IS NULL THEN MIN(@now - start_time, 300) ELSE end_time - start_time END)
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
                Browser = reader.IsDBNull(2) ? "" : reader.GetString(2),
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
                $"[{color}]{Markup.Escape(group.Key)}[/]",
                $"[{color}]{FormatDuration(totalDuration)}[/]"
            );

            if (showBrowser && group.Count() > 1)
            {
                foreach (var browserRow in group.OrderByDescending(x => x.TotalSeconds))
                {
                    if (string.IsNullOrEmpty(browserRow.Browser)) continue;
                    var bDuration = TimeSpan.FromSeconds(browserRow.TotalSeconds);
                    table.AddRow(
                        $"  [dim {color}]└─ {Markup.Escape(browserRow.Browser)}[/]",
                        $"[dim {color}]{FormatDuration(bDuration)}[/]"
                    );
                }
            }
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private static string FormatDuration(TimeSpan ts)
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
