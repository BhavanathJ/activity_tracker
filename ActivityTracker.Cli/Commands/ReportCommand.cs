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
        [Description("Date for raw dump, timeline, or coverage (YYYY-MM-DD, defaults to today)")]
        public string? Date { get; set; }

        [CommandOption("--coverage")]
        [Description("Audit browser event coverage against window focus and double-counting (YYYY-MM-DD via --date, defaults to today)")]
        public bool Coverage { get; set; }
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

        // Coverage audit
        if (settings.Coverage)
        {
            return ExecuteCoverage(settings.Date);
        }

        if (!settings.Today && !settings.Week && string.IsNullOrEmpty(settings.Month))
        {
            AnsiConsole.MarkupLine("[red]Please specify a timeframe: --today, --week, --month YYYY-MM, --timeline, --raw, or --coverage[/]");
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

    private int ExecuteCoverage(string? dateStr)
    {
        DateTime targetDate = DateTime.Today;
        if (!string.IsNullOrEmpty(dateStr))
        {
            if (!DateTime.TryParseExact(dateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out targetDate))
            {
                AnsiConsole.MarkupLine("[red]Invalid date format for --date. Use YYYY-MM-DD.[/]");
                return 1;
            }
        }

        var startDto = new DateTimeOffset(targetDate, DateTimeOffset.Now.Offset);
        var endDto = new DateTimeOffset(targetDate.AddDays(1), DateTimeOffset.Now.Offset);
        var startUnix = startDto.ToUnixTimeSeconds();
        var endUnix = endDto.ToUnixTimeSeconds();
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var dbManager = new DatabaseManager(readOnly: true);
        using var conn = dbManager.GetConnection();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, type, process_or_domain, browser, title, start_time, end_time
            FROM events
            WHERE ((start_time >= @start AND start_time < @end)
               OR (start_time < @start AND (end_time IS NULL OR end_time > @start)))
              AND type IN ('browser', 'window')
            ORDER BY start_time ASC;
        ";
        cmd.Parameters.AddWithValue("@start", startUnix);
        cmd.Parameters.AddWithValue("@end", endUnix);

        using var reader = cmd.ExecuteReader();
        var browserRows = new List<CoverageBrowserRow>();
        var allWindowRows = new List<CoverageWindowRow>();
        var windowRowsForDate = new List<CoverageWindowRow>();

        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            var type = reader.GetString(1);
            var processOrDomain = reader.GetString(2);
            var browser = reader.IsDBNull(3) ? null : reader.GetString(3);
            var title = reader.GetString(4);
            var startTime = reader.GetInt64(5);
            var endTime = reader.IsDBNull(6) ? (long?)null : reader.GetInt64(6);

            long effectiveEnd = endTime.HasValue ? endTime.Value : Math.Min(nowUnix, startTime + 300);
            if (effectiveEnd < startTime) effectiveEnd = startTime;

            if (type == "browser")
            {
                if (startTime >= startUnix && startTime < endUnix)
                {
                    browserRows.Add(new CoverageBrowserRow
                    {
                        Id = id,
                        Domain = processOrDomain,
                        Browser = browser,
                        Title = title,
                        StartTime = startTime,
                        EndTime = endTime,
                        EffectiveEnd = effectiveEnd
                    });
                }
            }
            else if (type == "window")
            {
                var winRow = new CoverageWindowRow
                {
                    Id = id,
                    Process = processOrDomain,
                    StartTime = startTime,
                    EndTime = endTime,
                    EffectiveEnd = effectiveEnd
                };
                allWindowRows.Add(winRow);

                if (startTime >= startUnix && startTime < endUnix && IsBrowserProcess(processOrDomain))
                {
                    windowRowsForDate.Add(winRow);
                }
            }
        }

        AnsiConsole.MarkupLine($"[bold white]Coverage & Overlap Audit: {targetDate:yyyy-MM-dd} ({TimeZoneInfo.Local.StandardName})[/]\n");

        if (browserRows.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]No browser events found for date {targetDate:yyyy-MM-dd}.[/]");
            return 0;
        }

        // 1. UNCOVERED: for each type='browser' row, compute seconds of the row NOT overlapped
        // by any type='window' row whose process maps to that row's browser.
        foreach (var bRow in browserRows)
        {
            var targetProc = MapBrowserToProcess(bRow.Browser);
            if (string.IsNullOrEmpty(targetProc) || bRow.Duration == 0)
            {
                bRow.UncoveredSeconds = bRow.Duration;
                continue;
            }

            var matchingWindows = allWindowRows
                .Where(w => w.Process.Equals(targetProc, StringComparison.OrdinalIgnoreCase))
                .Where(w => w.EffectiveEnd > bRow.StartTime && w.StartTime < bRow.EffectiveEnd)
                .ToList();

            var coveredIntervals = new List<(long Start, long End)>();
            foreach (var w in matchingWindows)
            {
                long oStart = Math.Max(bRow.StartTime, w.StartTime);
                long oEnd = Math.Min(bRow.EffectiveEnd, w.EffectiveEnd);
                if (oEnd > oStart)
                {
                    coveredIntervals.Add((oStart, oEnd));
                }
            }

            long coveredSecs = MergeAndSumIntervals(coveredIntervals);
            bRow.UncoveredSeconds = Math.Max(0, bRow.Duration - coveredSecs);
        }

        long totalUncoveredSeconds = browserRows.Sum(r => r.UncoveredSeconds);
        var top10Uncovered = browserRows
            .Where(r => r.UncoveredSeconds > 0)
            .OrderByDescending(r => r.UncoveredSeconds)
            .Take(10)
            .ToList();

        AnsiConsole.MarkupLine("[bold white]1. UNCOVERED BROWSER TIME[/]");
        AnsiConsole.MarkupLine($"Total Uncovered Seconds: [yellow]{totalUncoveredSeconds}s[/] ({FormatDuration(TimeSpan.FromSeconds(totalUncoveredSeconds))})");

        if (top10Uncovered.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]  No uncovered browser rows found (all browser events were covered by window focus).[/]\n");
        }
        else
        {
            var table = new Table()
                .BorderColor(Color.White)
                .Border(TableBorder.Rounded)
                .Title("[bold white]Top 10 Uncovered Browser Rows[/]");

            table.AddColumn(new TableColumn("[bold white]ID[/]").RightAligned());
            table.AddColumn(new TableColumn("[bold white]Browser[/]").LeftAligned());
            table.AddColumn(new TableColumn("[bold white]Domain[/]").LeftAligned());
            table.AddColumn(new TableColumn("[bold white]Start[/]").LeftAligned());
            table.AddColumn(new TableColumn("[bold white]End[/]").LeftAligned());
            table.AddColumn(new TableColumn("[bold white]Uncovered[/]").RightAligned());

            foreach (var row in top10Uncovered)
            {
                var startStr = DateTimeOffset.FromUnixTimeSeconds(row.StartTime).ToLocalTime().ToString("HH:mm:ss");
                var endStr = row.EndTime.HasValue
                    ? DateTimeOffset.FromUnixTimeSeconds(row.EndTime.Value).ToLocalTime().ToString("HH:mm:ss")
                    : "[italic grey](open)[/]";
                var uncStr = $"{row.UncoveredSeconds}s ({FormatDuration(TimeSpan.FromSeconds(row.UncoveredSeconds))})";

                table.AddRow(
                    row.Id.ToString(),
                    Markup.Escape(row.Browser ?? ""),
                    $"[green]{Markup.Escape(row.Domain)}[/]",
                    startStr,
                    endStr,
                    $"[yellow]{uncStr}[/]"
                );
            }

            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();
        }

        // 2. DOUBLE-COUNTED: for each browser, compute seconds where two or more type='browser' rows overlap.
        var browserGroups = browserRows
            .GroupBy(r => (r.Browser ?? "unknown").ToLowerInvariant())
            .OrderBy(g => g.Key);

        long totalDoubleCountedSeconds = 0;
        var perBrowserDoubleCounted = new Dictionary<string, long>();
        var allOverlappingPairs = new List<CoverageOverlappingPair>();

        foreach (var group in browserGroups)
        {
            var list = group.OrderBy(r => r.StartTime).ToList();

            var events = new List<(long Time, int Delta)>();
            foreach (var r in list)
            {
                if (r.EffectiveEnd > r.StartTime)
                {
                    events.Add((r.StartTime, +1));
                    events.Add((r.EffectiveEnd, -1));
                }
            }

            events.Sort((a, b) =>
            {
                int cmp = a.Time.CompareTo(b.Time);
                if (cmp != 0) return cmp;
                return a.Delta.CompareTo(b.Delta);
            });

            long overlapSecs = 0;
            int activeCount = 0;
            long lastTime = 0;

            foreach (var ev in events)
            {
                if (activeCount >= 2)
                {
                    long dt = ev.Time - lastTime;
                    if (dt > 0)
                    {
                        overlapSecs += dt;
                    }
                }
                activeCount += ev.Delta;
                lastTime = ev.Time;
            }

            perBrowserDoubleCounted[group.Key] = overlapSecs;
            totalDoubleCountedSeconds += overlapSecs;

            // Pairwise overlap
            for (int i = 0; i < list.Count; i++)
            {
                var r1 = list[i];
                for (int j = i + 1; j < list.Count; j++)
                {
                    var r2 = list[j];
                    if (r2.StartTime >= r1.EffectiveEnd)
                    {
                        break;
                    }

                    long pairOverlapStart = Math.Max(r1.StartTime, r2.StartTime);
                    long pairOverlapEnd = Math.Min(r1.EffectiveEnd, r2.EffectiveEnd);
                    long pairOverlap = pairOverlapEnd - pairOverlapStart;
                    if (pairOverlap > 0)
                    {
                        allOverlappingPairs.Add(new CoverageOverlappingPair
                        {
                            Browser = group.Key,
                            Id1 = r1.Id,
                            Id2 = r2.Id,
                            Domain1 = r1.Domain,
                            Domain2 = r2.Domain,
                            OverlapSeconds = pairOverlap
                        });
                    }
                }
            }
        }

        AnsiConsole.MarkupLine("[bold white]2. DOUBLE-COUNTED BROWSER TIME[/]");
        string breakdown = string.Join(", ", perBrowserDoubleCounted.Select(kv => $"{kv.Key}: {kv.Value}s"));
        string breakdownSuffix = perBrowserDoubleCounted.Count > 1 ? $" [dim]({breakdown})[/]" : "";
        AnsiConsole.MarkupLine($"Total Double-Counted Seconds: [yellow]{totalDoubleCountedSeconds}s[/] ({FormatDuration(TimeSpan.FromSeconds(totalDoubleCountedSeconds))}){breakdownSuffix}");

        var top10Pairs = allOverlappingPairs
            .OrderByDescending(p => p.OverlapSeconds)
            .Take(10)
            .ToList();

        if (top10Pairs.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]  No overlapping browser rows found (no double-counting).[/]\n");
        }
        else
        {
            var pairTable = new Table()
                .BorderColor(Color.White)
                .Border(TableBorder.Rounded)
                .Title("[bold white]Top 10 Overlapping Browser Row Pairs[/]");

            pairTable.AddColumn(new TableColumn("[bold white]Browser[/]").LeftAligned());
            pairTable.AddColumn(new TableColumn("[bold white]Row IDs[/]").LeftAligned());
            pairTable.AddColumn(new TableColumn("[bold white]Domains[/]").LeftAligned());
            pairTable.AddColumn(new TableColumn("[bold white]Overlap[/]").RightAligned());

            foreach (var pair in top10Pairs)
            {
                var overlapStr = $"{pair.OverlapSeconds}s ({FormatDuration(TimeSpan.FromSeconds(pair.OverlapSeconds))})";
                pairTable.AddRow(
                    Markup.Escape(pair.Browser),
                    $"#{pair.Id1} & #{pair.Id2}",
                    $"{Markup.Escape(pair.Domain1)} [dim]vs[/] {Markup.Escape(pair.Domain2)}",
                    $"[yellow]{overlapStr}[/]"
                );
            }

            AnsiConsole.Write(pairTable);
            AnsiConsole.WriteLine();
        }

        // 3. RECONCILIATION: browser-domain total, browser-window total, and the difference explained by (uncovered, overlap).
        long browserDomainTotal = browserRows.Sum(r => r.Duration);
        long browserWindowTotal = windowRowsForDate.Sum(w => w.Duration);
        long diff = browserDomainTotal - browserWindowTotal;

        // Window time with no active browser domain
        long windowWithoutDomainSeconds = 0;
        foreach (var w in windowRowsForDate)
        {
            var matchingBrowserRows = browserRows
                .Where(b => string.Equals(MapBrowserToProcess(b.Browser), w.Process, StringComparison.OrdinalIgnoreCase))
                .Where(b => b.EffectiveEnd > w.StartTime && b.StartTime < w.EffectiveEnd)
                .ToList();

            var overlapList = new List<(long Start, long End)>();
            foreach (var b in matchingBrowserRows)
            {
                long s = Math.Max(w.StartTime, b.StartTime);
                long e = Math.Min(w.EffectiveEnd, b.EffectiveEnd);
                if (e > s) overlapList.Add((s, e));
            }

            long domainCoveredWindow = MergeAndSumIntervals(overlapList);
            windowWithoutDomainSeconds += Math.Max(0, w.Duration - domainCoveredWindow);
        }

        AnsiConsole.MarkupLine("[bold white]3. RECONCILIATION[/]");

        var reconTable = new Table()
            .BorderColor(Color.White)
            .Border(TableBorder.Rounded)
            .Title("[bold white]Totals & Reconciliation[/]");

        reconTable.AddColumn(new TableColumn("[bold white]Metric[/]").LeftAligned());
        reconTable.AddColumn(new TableColumn("[bold white]Duration[/]").RightAligned());
        reconTable.AddColumn(new TableColumn("[bold white]Seconds[/]").RightAligned());
        reconTable.AddColumn(new TableColumn("[bold white]Note[/]").LeftAligned());

        reconTable.AddRow("Browser Domain Total", FormatDuration(TimeSpan.FromSeconds(browserDomainTotal)), $"{browserDomainTotal}s", "Sum of all type='browser' event durations");
        reconTable.AddRow("Browser Window Total", FormatDuration(TimeSpan.FromSeconds(browserWindowTotal)), $"{browserWindowTotal}s", "Sum of window focus durations (brave, chrome, msedge)");

        string diffSign = diff >= 0 ? "+" : "-";
        string diffColor = diff == 0 ? "green" : (diff > 0 ? "yellow" : "cyan");
        reconTable.AddRow($"[{diffColor}]Difference[/]", $"[{diffColor}]{diffSign}{FormatDuration(TimeSpan.FromSeconds(Math.Abs(diff)))}[/]", $"[{diffColor}]{diffSign}{Math.Abs(diff)}s[/]", $"[{diffColor}]browser-domain - browser-window[/]");

        AnsiConsole.Write(reconTable);
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("[bold white]Difference Explanation:[/] ");
        AnsiConsole.MarkupLine($"  [yellow]+[/] Uncovered browser time (outside window focus): [yellow]{totalUncoveredSeconds}s[/] ({FormatDuration(TimeSpan.FromSeconds(totalUncoveredSeconds))})");
        AnsiConsole.MarkupLine($"  [yellow]+[/] Double-counted browser time (concurrent tabs):    [yellow]{totalDoubleCountedSeconds}s[/] ({FormatDuration(TimeSpan.FromSeconds(totalDoubleCountedSeconds))})");
        if (windowWithoutDomainSeconds > 0)
        {
            AnsiConsole.MarkupLine($"  [cyan]-[/] Browser window focus without active domain:     [cyan]{windowWithoutDomainSeconds}s[/] ({FormatDuration(TimeSpan.FromSeconds(windowWithoutDomainSeconds))})");
        }

        long netExplained = totalUncoveredSeconds + totalDoubleCountedSeconds - windowWithoutDomainSeconds;
        AnsiConsole.MarkupLine($"  Net explained difference: [bold white]{netExplained:+0;-0;0}s[/] (actual difference: [bold white]{diff:+0;-0;0}s[/])");
        AnsiConsole.WriteLine();

        return 0;
    }

    private static string? MapBrowserToProcess(string? browser)
    {
        if (string.IsNullOrWhiteSpace(browser)) return null;
        var b = browser.Trim().ToLowerInvariant();
        return b switch
        {
            "brave" => "brave",
            "chrome" => "chrome",
            "edge" => "msedge",
            "msedge" => "msedge",
            _ => b
        };
    }

    private static bool IsBrowserProcess(string process)
    {
        return process.Equals("brave", StringComparison.OrdinalIgnoreCase) ||
               process.Equals("chrome", StringComparison.OrdinalIgnoreCase) ||
               process.Equals("msedge", StringComparison.OrdinalIgnoreCase);
    }

    private static long MergeAndSumIntervals(List<(long Start, long End)> intervals)
    {
        var valid = intervals.Where(i => i.End > i.Start).ToList();
        if (valid.Count == 0) return 0;
        valid.Sort((a, b) => a.Start.CompareTo(b.Start));

        long total = 0;
        long curStart = valid[0].Start;
        long curEnd = valid[0].End;

        for (int i = 1; i < valid.Count; i++)
        {
            if (valid[i].Start <= curEnd)
            {
                curEnd = Math.Max(curEnd, valid[i].End);
            }
            else
            {
                total += (curEnd - curStart);
                curStart = valid[i].Start;
                curEnd = valid[i].End;
            }
        }
        total += (curEnd - curStart);
        return total;
    }

    private class CoverageBrowserRow
    {
        public long Id { get; set; }
        public string Domain { get; set; } = "";
        public string? Browser { get; set; }
        public string Title { get; set; } = "";
        public long StartTime { get; set; }
        public long? EndTime { get; set; }
        public long EffectiveEnd { get; set; }
        public long Duration => Math.Max(0, EffectiveEnd - StartTime);
        public long UncoveredSeconds { get; set; }
    }

    private class CoverageWindowRow
    {
        public long Id { get; set; }
        public string Process { get; set; } = "";
        public long StartTime { get; set; }
        public long? EndTime { get; set; }
        public long EffectiveEnd { get; set; }
        public long Duration => Math.Max(0, EffectiveEnd - StartTime);
    }

    private class CoverageOverlappingPair
    {
        public string Browser { get; set; } = "";
        public long Id1 { get; set; }
        public long Id2 { get; set; }
        public string Domain1 { get; set; } = "";
        public string Domain2 { get; set; } = "";
        public long OverlapSeconds { get; set; }
    }

    private class ReportRow
    {
        public string Category { get; set; } = "";
        public string Key { get; set; } = "";
        public string? Browser { get; set; }
        public long TotalSeconds { get; set; }
    }
}
