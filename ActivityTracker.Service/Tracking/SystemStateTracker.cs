using System;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using ActivityTracker.Core.Data;
using ActivityTracker.Core.Models;
using ActivityTracker.Service.Http;

namespace ActivityTracker.Service.Tracking;

public class SystemStateTracker
{
    private readonly ILogger _logger;
    private readonly WindowSessionManager _windowSessionManager;
    private readonly DatabaseManager _dbManager;
    private readonly ExtensionListener _extensionListener;
    private const int WTS_SESSION_LOCK = 0x7;
    private const int WTS_SESSION_UNLOCK = 0x8;
    private const int PBT_APMSUSPEND = 0x0004;
    private const int PBT_APMRESUMEAUTOMATIC = 0x0012;
    private const int PBT_APMRESUMESUSPEND = 0x0007;

    public SystemStateTracker(ILogger logger, WindowSessionManager windowSessionManager, DatabaseManager dbManager, ExtensionListener extensionListener)
    {
        _logger = logger;
        _windowSessionManager = windowSessionManager;
        _dbManager = dbManager;
        _extensionListener = extensionListener;
    }

    public void Start()
    {
        _logger.LogInformation("Starting System State Tracker...");

        SessionChangeNotifier.OnSessionChange += HandleSessionChange;
        PowerChangeNotifier.OnPowerChange += HandlePowerChange;
    }

    public void Stop()
    {
        SessionChangeNotifier.OnSessionChange -= HandleSessionChange;
        PowerChangeNotifier.OnPowerChange -= HandlePowerChange;

        _logger.LogInformation("System State Tracker stopped.");
    }

    private void HandleSessionChange(int reason)
    {
        var now = DateTimeOffset.UtcNow;

        if (reason == WTS_SESSION_LOCK)
        {
            _logger.LogInformation("System Locked.");
            _windowSessionManager.SetLocked(true);
            _windowSessionManager.CloseCurrentSession(now);
            // Audio sessions (podcasts, YouTube) are intentionally left running while locked.
            _extensionListener.CloseAllBrowserSessions(now);

            var record = new EventRecord
            {
                Type = "lock",
                ProcessOrDomain = "Locked",
                Title = "Locked",
                StartTime = now.ToUnixTimeSeconds()
            };
            _extensionListener.LockSessionId = _dbManager.InsertEvent(record);
        }
        else if (reason == WTS_SESSION_UNLOCK)
        {
            _logger.LogInformation("System Unlocked.");
            if (_extensionListener.LockSessionId.HasValue)
            {
                _dbManager.UpdateEventEndTime(_extensionListener.LockSessionId.Value, now.ToUnixTimeSeconds());
                _extensionListener.LockSessionId = null;
            }
            _windowSessionManager.SetLocked(false);
            _windowSessionManager.ForceReevaluate();
        }
    }

    private void HandlePowerChange(int eventType)
    {
        if (eventType == PBT_APMSUSPEND)
        {
            _logger.LogInformation("System Suspending.");
            var now = DateTimeOffset.UtcNow;
            _windowSessionManager.CloseCurrentSession(now);
            // On suspend close everything including audio.
            _extensionListener.CloseAllBrowserAndAudioSessions(now);
        }
        else if (eventType == PBT_APMRESUMEAUTOMATIC || eventType == PBT_APMRESUMESUSPEND)
        {
            _logger.LogInformation("System Resumed.");
            _windowSessionManager.ForceReevaluate();
        }
    }
}
