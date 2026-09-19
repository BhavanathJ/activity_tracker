using System;
using System.Threading;
using ActivityTracker.Core.Configuration;

namespace ActivityTracker.SessionAgent;

public class Program
{
    public static void Main(string[] args)
    {
        var config = ConfigManager.Load();
        var monitor = new IdleMonitor(config.IdleTimeoutSeconds, config.HttpPort);

        monitor.Start();

        // Block until the process is killed (Task Scheduler or manual termination)
        using var exitEvent = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            exitEvent.Set();
        };
        exitEvent.Wait();

        monitor.Stop();
    }
}
