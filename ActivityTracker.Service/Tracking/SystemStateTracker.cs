using System;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using ActivityTracker.Core.Data;
using ActivityTracker.Core.Models;

namespace ActivityTracker.Service.Tracking;

public class SystemStateTracker
{
    private readonly ILogger _logger;
    private readonly WindowTracker _windowTracker;
    private readonly DatabaseManager _dbManager;
    private long? _lockSessionId;
    private const int WTS_SESSION_LOCK = 0x7;
    private const int WTS_SESSION_UNLOCK = 0x8;
    private const int PBT_APMSUSPEND = 0x0004;
    private const int PBT_APMRESUMEAUTOMATIC = 0x0012;

    public SystemStateTracker(ILogger logger, WindowTracker windowTracker, DatabaseManager dbManager)
    {
        _logger = logger;
        _windowTracker = windowTracker;
        _dbManager = dbManager;
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
            _windowTracker.CloseCurrentSession(now);

            var record = new EventRecord
            {
                Type = "lock",
                ProcessOrDomain = "Locked",
                Title = "Locked",
                StartTime = now.ToUnixTimeSeconds()
            };
            _lockSessionId = _dbManager.InsertEvent(record);
        }
        else if (reason == WTS_SESSION_UNLOCK)
        {
            _logger.LogInformation("System Unlocked.");
            if (_lockSessionId.HasValue)
            {
                _dbManager.UpdateEventEndTime(_lockSessionId.Value, now.ToUnixTimeSeconds());
                _lockSessionId = null;
            }
            _windowTracker.ForceReevaluate();
        }
    }

    private void HandlePowerChange(int eventType)
    {
        if (eventType == PBT_APMSUSPEND)
        {
            _logger.LogInformation("System Suspending.");
            _windowTracker.CloseCurrentSession(DateTimeOffset.UtcNow);
        }
        else if (eventType == PBT_APMRESUMEAUTOMATIC)
        {
            _logger.LogInformation("System Resumed.");
            _windowTracker.ForceReevaluate();
        }
    }

}
