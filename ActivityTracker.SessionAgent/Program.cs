using System;
using System.Threading;
using Microsoft.Win32;
using ActivityTracker.Core.Configuration;

namespace ActivityTracker.SessionAgent;

public class Program
{
    public static void Main(string[] args)
    {
        FileLogger.LogInfo("SessionAgent starting.");
        var config = ConfigManager.Load();
        var windowTracker = new WindowTracker(config);
        var monitor = new IdleMonitor(config.IdleTimeoutSeconds, config.HttpPort, windowTracker);
        var mediaMonitor = new MediaMonitor(config.HttpPort);

        monitor.Start();
        windowTracker.Start();
        mediaMonitor.Start();

        // Subscribe to SystemEvents for lock/unlock and suspend/resume
        SystemEvents.SessionSwitch += (s, e) =>
        {
            if (e.Reason == SessionSwitchReason.SessionUnlock)
            {
                FileLogger.LogInfo("SessionAgent detected unlock. Forcing window report.");
                windowTracker.ForceReport();
            }
        };

        SystemEvents.PowerModeChanged += (s, e) =>
        {
            if (e.Mode == PowerModes.Resume)
            {
                FileLogger.LogInfo("SessionAgent detected resume. Forcing window report.");
                windowTracker.ForceReport();
            }
        };

        // Block until the process is killed (Task Scheduler, logoff, or manual termination)
        using var exitEvent = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            exitEvent.Set();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            exitEvent.Set();
        };
        exitEvent.Wait();

        mediaMonitor.Stop();
        windowTracker.Stop();
        monitor.Stop();
        FileLogger.LogInfo("SessionAgent stopped.");
    }
}
