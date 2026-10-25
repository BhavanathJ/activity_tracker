using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Cli;
using ActivityTracker.Core.Data;

namespace ActivityTracker.Cli.Commands;

public class LiveCommand : Command<LiveCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandOption("--interval <SECONDS>")]
        [Description("Refresh interval in seconds (1-30, default 2)")]
        [DefaultValue(2)]
        public int Interval { get; set; } = 2;

        public override ValidationResult Validate()
        {
            if (Interval < 1) Interval = 1;
            if (Interval > 30) Interval = 30;
            return ValidationResult.Success();
        }
    }

    private const int StaleThresholdSeconds = 75;
    private const int OpenRowStaleCap = 300;

    public override int Execute(CommandContext context, Settings settings)
    {
        var dbManager = new DatabaseManager(readOnly: true);

        // If output is redirected or not interactive, print one snapshot and exit
        if (Console.IsOutputRedirected || !AnsiConsole.Profile.Capabilities.Interactive)
        {
            var table = BuildLiveTable(dbManager, settings.Interval, out _);
            AnsiConsole.Write(table ?? new Table().AddColumn("info").AddRow("Waiting for database..."));
            return 0;
        }

        // Set up Ctrl+C for clean exit
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        Table? lastGoodSnapshot = null;

        AnsiConsole.Live(new Text("Loading..."))
            .AutoClear(false)
            .Overflow(VerticalOverflow.Ellipsis)
            .Start(ctx =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var table = BuildLiveTable(dbManager, settings.Interval, out var dbError);

                    if (table != null)
                    {
                        lastGoodSnapshot = table;
                        ctx.UpdateTarget(table);
                    }
                    else if (lastGoodSnapshot != null)
                    {
                        // Re-render last good snapshot with error footer appended
                        var fallback = CloneWithErrorFooter(lastGoodSnapshot, dbError!);
                        ctx.UpdateTarget(fallback);
                    }
                    else
                    {
                        // No successful snapshot yet
                        ctx.UpdateTarget(new Markup("[yellow]Waiting for database...[/]"));
                    }

                    ctx.Refresh();

                    if (cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(settings.Interval)))
                        break;
                }
            });

        // Restore cursor visibility on exit
        AnsiConsole.Cursor.Show();
        return 0;
    }

    /// <summary>
    /// Returns null if a SqliteException prevents reading data.
    /// On success, dbError is null. On failure, dbError contains the message for the footer.
    /// </summary>
    private static Table? BuildLiveTable(DatabaseManager dbManager, int interval, out string? dbError)
    {
        dbError = null;
        var nowUtc = DateTimeOffset.UtcNow;
        var nowUnix = nowUtc.ToUnixTimeSeconds();
        var localNow = nowUtc.ToLocalTime();

        // Calculate today's midnight in unix seconds
        var localMidnight = new DateTimeOffset(localNow.Date, localNow.Offset);
        var todayStartUnix = localMidnight.ToUnixTimeSeconds();

        // Data containers
        long? lastSeen = null;
        bool healthOk = false;
        string healthText = "";
        string stateText = "";
        string stateColor = "white";

        // Open rows for "Now" panel
        var openWindows = new List<EventRow>();
        var openBrowsers = new List<EventRow>();
        var openAudios = new List<EventRow>();
        bool hasOpenLock = false;
        long lockStartTime = 0;

        // Today totals
        var appTotals = new Dictionary<string, long>();
        var domainTotals = new Dictionary<string, long>();
        var audioTotals = new Dictionary<string, long>();
        long lockedTotal = 0;

        // Recent events
        var recentEvents = new List<EventRow>();

        try
        {
            using var conn = dbManager.GetConnection();

            // 1. Read last_seen heartbeat
            try
            {
                using var metaCmd = conn.CreateCommand();
                metaCmd.CommandText = "SELECT value FROM meta WHERE key = 'last_seen';";
                var result = metaCmd.ExecuteScalar();
                if (result != null && result != DBNull.Value)
                {
                    lastSeen = Convert.ToInt64(result);
                }
            }
            catch (SqliteException)
            {
                // meta table may not exist yet; treat as no heartbeat
            }

            // Compute health
            if (lastSeen.HasValue)
            {
                var ageSec = nowUnix - lastSeen.Value;
                if (ageSec <= StaleThresholdSeconds)
                {
                    healthOk = true;
                    healthText = $"[green]Service OK[/] [dim](heartbeat {ageSec}s ago)[/]";
                }
                else
                {
                    var ageMinutes = ageSec / 60;
                    healthText = $"[red]Service STALE[/] [dim](last heartbeat {ageMinutes}m ago) - tracker may be stopped[/]";
                }
            }
            else
            {
                healthText = "[red]Service STALE[/] [dim](no heartbeat found) - tracker may be stopped[/]";
            }

            // 2. Read open rows (end_time IS NULL)
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT type, process_or_domain, browser, title, start_time
                    FROM events
                    WHERE end_time IS NULL
                    ORDER BY start_time DESC;";

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var row = new EventRow
                    {
                        Type = reader.GetString(0),
                        ProcessOrDomain = reader.GetString(1),
                        Browser = reader.IsDBNull(2) ? null : reader.GetString(2),
                        Title = reader.GetString(3),
                        StartTime = reader.GetInt64(4),
                        EndTime = null
                    };

                    switch (row.Type)
                    {
                        case "lock":
                            hasOpenLock = true;
                            lockStartTime = row.StartTime;
                            break;
                        case "window":
                            openWindows.Add(row);
                            break;
                        case "browser":
                            openBrowsers.Add(row);
                            break;
                        case "audio":
                            openAudios.Add(row);
                            break;
                    }
                }
            }

            // 3. Today's data (since local midnight) - all rows with start_time >= todayStart
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT type, process_or_domain, browser, start_time, end_time
                    FROM events
                    WHERE start_time >= @todayStart
                    ORDER BY start_time DESC;";
                cmd.Parameters.AddWithValue("@todayStart", todayStartUnix);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var type = reader.GetString(0);
                    var key = reader.GetString(1);
                    var browser = reader.IsDBNull(2) ? null : reader.GetString(2);
                    var startTime = reader.GetInt64(3);
                    var endTime = reader.IsDBNull(4) ? (long?)null : reader.GetInt64(4);

                    long duration;
                    if (endTime.HasValue)
                    {
                        duration = endTime.Value - startTime;
                    }
                    else
                    {
                        // Open row: if health OK use live duration, else cap at 300s
                        duration = healthOk
                            ? Math.Max(0, nowUnix - startTime)
                            : Math.Min(Math.Max(0, nowUnix - startTime), OpenRowStaleCap);
                    }

                    if (duration < 0) duration = 0;

                    switch (type)
                    {
                        case "window":
                            appTotals[key] = appTotals.GetValueOrDefault(key) + duration;
                            break;
                        case "browser":
                            domainTotals[key] = domainTotals.GetValueOrDefault(key) + duration;
                            break;
                        case "audio":
                            var audioKey = browser != null ? $"{key} ({browser})" : key;
                            audioTotals[audioKey] = audioTotals.GetValueOrDefault(audioKey) + duration;
                            break;
                        case "lock":
                            lockedTotal += duration;
                            break;
                    }
                }
            }

            // 4. Recent events (last 8 by start_time DESC)
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT type, process_or_domain, browser, title, start_time, end_time
                    FROM events
                    WHERE start_time >= @todayStart
                    ORDER BY start_time DESC
                    LIMIT 8;";
                cmd.Parameters.AddWithValue("@todayStart", todayStartUnix);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    recentEvents.Add(new EventRow
                    {
                        Type = reader.GetString(0),
                        ProcessOrDomain = reader.GetString(1),
                        Browser = reader.IsDBNull(2) ? null : reader.GetString(2),
                        Title = reader.GetString(3),
                        StartTime = reader.GetInt64(4),
                        EndTime = reader.IsDBNull(5) ? (long?)null : reader.GetInt64(5)
                    });
                }
            }
        }
        catch (SqliteException ex) when (IsSqliteBusy(ex))
        {
            dbError = "DB busy, retrying...";
            return null;
        }
        catch (SqliteException ex)
        {
            dbError = $"DB error ({ex.SqliteErrorCode}), retrying...";
            return null;
        }

        // Derive state
        if (!healthOk)
        {
            stateText = "UNKNOWN (service stale)";
            stateColor = "grey";
        }
        else if (hasOpenLock)
        {
            var lockTime = DateTimeOffset.FromUnixTimeSeconds(lockStartTime).ToLocalTime();
            stateText = $"LOCKED since {lockTime:HH:mm:ss}";
            stateColor = "red";
        }
        else if (openWindows.Count > 0)
        {
            stateText = "ACTIVE";
            stateColor = "green";
        }
        else
        {
            stateText = "IDLE / AWAY";
            stateColor = "yellow";
        }

        // Build the composite table
        return ComposeLayout(
            localNow, healthText, interval, stateText, stateColor,
            openWindows, openBrowsers, openAudios, nowUnix,
            appTotals, domainTotals, audioTotals, lockedTotal,
            recentEvents, dbError);
    }

    private static Table ComposeLayout(
        DateTimeOffset localNow, string healthText, int interval,
        string stateText, string stateColor,
        List<EventRow> openWindows, List<EventRow> openBrowsers, List<EventRow> openAudios,
        long nowUnix,
        Dictionary<string, long> appTotals, Dictionary<string, long> domainTotals,
        Dictionary<string, long> audioTotals, long lockedTotal,
        List<EventRow> recentEvents, string? dbError)
    {
        var root = new Table()
            .Border(TableBorder.Heavy)
            .BorderColor(Color.Grey35)
            .Width(110)
            .HideHeaders()
            .AddColumn(new TableColumn("content").NoWrap());

        // ── Panel 1: Header ──
        root.AddRow(new Markup(
            $"[bold white]Activity Tracker Live[/]  " +
            $"[dim]{Markup.Escape(localNow.ToString("yyyy-MM-dd HH:mm:ss"))}[/]  " +
            $"{healthText}  " +
            $"[dim](every {interval}s)[/]"));

        // ── Panel 2: State ──
        root.AddRow(new Markup($"[bold]State:[/] [{stateColor}]{Markup.Escape(stateText)}[/]"));

        // ── Panel 3: Now ──
        var nowPanel = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey50)
            .Title("[bold cyan]Now[/]")
            .AddColumn(new TableColumn("[bold]Type[/]").Width(10))
            .AddColumn(new TableColumn("[bold]App / Domain[/]").Width(30))
            .AddColumn(new TableColumn("[bold]Title[/]").Width(45))
            .AddColumn(new TableColumn("[bold]For[/]").Width(10).RightAligned());

        // Current window
        if (openWindows.Count > 0)
        {
            var w = openWindows[0]; // latest by start_time DESC
            var dur = FormatElapsed(nowUnix - w.StartTime);
            nowPanel.AddRow(
                "[cyan]window[/]",
                $"[cyan]{Markup.Escape(w.ProcessOrDomain)}[/]",
                Markup.Escape(Truncate(w.Title, 44)),
                $"[dim]{dur}[/]");
        }
        else
        {
            nowPanel.AddRow("[dim]window[/]", "[dim]-[/]", "[dim]-[/]", "[dim]-[/]");
        }

        // Open browsers
        if (openBrowsers.Count > 0)
        {
            foreach (var b in openBrowsers)
            {
                var dur = FormatElapsed(nowUnix - b.StartTime);
                var browserLabel = b.Browser != null ? $" [dim]({Markup.Escape(b.Browser)})[/]" : "";
                nowPanel.AddRow(
                    "[green]browser[/]",
                    $"[green]{Markup.Escape(b.ProcessOrDomain)}[/]{browserLabel}",
                    Markup.Escape(Truncate(b.Title, 44)),
                    $"[dim]{dur}[/]");
            }
        }
        else
        {
            nowPanel.AddRow("[dim]browser[/]", "[dim]-[/]", "[dim]-[/]", "[dim]-[/]");
        }

        // Open audio
        if (openAudios.Count > 0)
        {
            foreach (var a in openAudios)
            {
                var dur = FormatElapsed(nowUnix - a.StartTime);
                var browserLabel = a.Browser != null ? $" ({Markup.Escape(a.Browser)})" : "";
                nowPanel.AddRow(
                    "[yellow]audio[/]",
                    $"[yellow]{Markup.Escape(a.ProcessOrDomain)}[/][dim]{browserLabel}[/]",
                    "[dim]-[/]",
                    $"[dim]{dur}[/]");
            }
        }
        else
        {
            nowPanel.AddRow("[dim]audio[/]", "[dim]-[/]", "[dim]-[/]", "[dim]-[/]");
        }

        root.AddRow(nowPanel);

        // ── Panel 4: Today so far ──
        var todayPanel = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey50)
            .Title("[bold white]Today So Far[/]")
            .HideHeaders()
            .AddColumn(new TableColumn("c1").Width(36))
            .AddColumn(new TableColumn("c2").Width(36))
            .AddColumn(new TableColumn("c3").Width(36));

        // Top 5 Apps
        var appsTable = new Table()
            .Border(TableBorder.Simple)
            .Title("[bold cyan]Apps (window)[/]")
            .AddColumn(new TableColumn("[bold]App[/]").Width(22))
            .AddColumn(new TableColumn("[bold]Time[/]").Width(10).RightAligned());

        var topApps = appTotals.OrderByDescending(kv => kv.Value).Take(5);
        foreach (var kv in topApps)
        {
            appsTable.AddRow(
                $"[cyan]{Markup.Escape(Truncate(kv.Key, 21))}[/]",
                $"[cyan]{FormatDuration(kv.Value)}[/]");
        }
        if (!appTotals.Any()) appsTable.AddRow("[dim]-[/]", "[dim]-[/]");

        // Top 5 Domains
        var domainsTable = new Table()
            .Border(TableBorder.Simple)
            .Title("[bold green]Domains (browser)[/]")
            .AddColumn(new TableColumn("[bold]Domain[/]").Width(22))
            .AddColumn(new TableColumn("[bold]Time[/]").Width(10).RightAligned());

        var topDomains = domainTotals.OrderByDescending(kv => kv.Value).Take(5);
        foreach (var kv in topDomains)
        {
            domainsTable.AddRow(
                $"[green]{Markup.Escape(Truncate(kv.Key, 21))}[/]",
                $"[green]{FormatDuration(kv.Value)}[/]");
        }
        if (!domainTotals.Any()) domainsTable.AddRow("[dim]-[/]", "[dim]-[/]");

        // Top 5 Audio
        var audioTable = new Table()
            .Border(TableBorder.Simple)
            .Title("[bold yellow]Audio[/]")
            .AddColumn(new TableColumn("[bold]Source[/]").Width(22))
            .AddColumn(new TableColumn("[bold]Time[/]").Width(10).RightAligned());

        var topAudio = audioTotals.OrderByDescending(kv => kv.Value).Take(5);
        foreach (var kv in topAudio)
        {
            audioTable.AddRow(
                $"[yellow]{Markup.Escape(Truncate(kv.Key, 21))}[/]",
                $"[yellow]{FormatDuration(kv.Value)}[/]");
        }
        if (!audioTotals.Any()) audioTable.AddRow("[dim]-[/]", "[dim]-[/]");

        todayPanel.AddRow(appsTable, domainsTable, audioTable);
        root.AddRow(todayPanel);

        // Locked total line
        if (lockedTotal > 0)
        {
            root.AddRow(new Markup($"  [grey50]Locked total: {FormatDuration(lockedTotal)}[/]"));
        }

        // ── Panel 5: Recent events ──
        var recentTable = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey50)
            .Title("[bold white]Recent Events[/]")
            .AddColumn(new TableColumn("[bold]Type[/]").Width(8))
            .AddColumn(new TableColumn("[bold]Start[/]").Width(10))
            .AddColumn(new TableColumn("[bold]Duration[/]").Width(10).RightAligned())
            .AddColumn(new TableColumn("[bold]App / Domain[/]").Width(30));

        foreach (var ev in recentEvents)
        {
            var startStr = DateTimeOffset.FromUnixTimeSeconds(ev.StartTime).ToLocalTime().ToString("HH:mm:ss");
            string durStr;
            if (ev.EndTime.HasValue)
            {
                durStr = FormatDuration(ev.EndTime.Value - ev.StartTime);
            }
            else
            {
                durStr = "[italic](open)[/]";
            }

            string typeColor = ev.Type switch
            {
                "window" => "cyan",
                "browser" => "green",
                "audio" => "yellow",
                "lock" => "grey50",
                _ => "white"
            };

            var appDomain = Markup.Escape(Truncate(ev.ProcessOrDomain, 29));
            if (ev.Browser != null)
            {
                appDomain += $" [dim]({Markup.Escape(ev.Browser)})[/]";
            }

            recentTable.AddRow(
                $"[{typeColor}]{Markup.Escape(ev.Type)}[/]",
                startStr,
                durStr,
                $"[{typeColor}]{appDomain}[/]");
        }

        if (recentEvents.Count == 0)
        {
            recentTable.AddRow("[dim]-[/]", "[dim]-[/]", "[dim]-[/]", "[dim]-[/]");
        }

        root.AddRow(recentTable);

        // Footer
        var footerParts = new List<string> { "[dim]live totals include in-progress rows · Ctrl+C to exit[/]" };
        if (dbError != null)
        {
            footerParts.Insert(0, $"[bold yellow]{Markup.Escape(dbError)}[/]");
        }
        root.AddRow(new Markup(string.Join("  ", footerParts)));

        return root;
    }

    /// <summary>
    /// Creates a simple renderable that shows the last good snapshot info plus an error footer.
    /// We cannot deep-clone a Spectre Table, so we wrap the error in a new single-row table.
    /// </summary>
    private static IRenderable CloneWithErrorFooter(Table lastGood, string errorMessage)
    {
        // Re-render the last good table as-is by wrapping in a Rows composite
        // with the error appended underneath.
        return new Rows(
            lastGood,
            new Markup($"[bold yellow]{Markup.Escape(errorMessage)}[/]"));
    }

    private static string FormatElapsed(long seconds)
    {
        if (seconds < 0) seconds = 0;
        var m = seconds / 60;
        var s = seconds % 60;
        return $"{m:00}:{s:00}";
    }

    private static string FormatDuration(long totalSeconds)
    {
        if (totalSeconds < 0) totalSeconds = 0;
        var ts = TimeSpan.FromSeconds(totalSeconds);
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        return $"{ts.Minutes}m {ts.Seconds}s";
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= maxLength ? value : value[..(maxLength - 1)] + "…";
    }

    private static bool IsSqliteBusy(SqliteException ex)
    {
        const int SqliteBusyCode = 5;
        return ex.SqliteErrorCode == SqliteBusyCode || (ex.SqliteExtendedErrorCode & 0xFF) == SqliteBusyCode;
    }

    private record EventRow
    {
        public required string Type { get; init; }
        public required string ProcessOrDomain { get; init; }
        public string? Browser { get; init; }
        public required string Title { get; init; }
        public long StartTime { get; init; }
        public long? EndTime { get; init; }
    }
}
