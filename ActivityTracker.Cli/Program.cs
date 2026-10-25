using System;
using Spectre.Console.Cli;
using ActivityTracker.Cli.Commands;

namespace ActivityTracker.Cli;

public class Program
{
    public static int Main(string[] args)
    {
        var app = new CommandApp();
        app.Configure(config =>
        {
            config.AddCommand<ReportCommand>("report")
                .WithDescription("Generates an activity report.");
            config.AddCommand<LiveCommand>("live")
                .WithDescription("Shows what the tracker is recording right now, refreshing in place.");
        });

        return app.Run(args);
    }
}
