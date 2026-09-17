using System;
using Microsoft.Win32;
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

    public SystemStateTracker(ILogger logger, WindowTracker windowTracker, DatabaseManager dbManager)
    {
        _logger = logger;
        _windowTracker = windowTracker;
        _dbManager = dbManager;
    }

    public void Start()
    {
        _logger.LogInformation("Starting System State Tracker...");
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    public void Stop()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _logger.LogInformation("System State Tracker stopped.");
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        var now = DateTimeOffset.UtcNow;
        if (e.Reason == SessionSwitchReason.SessionLock)
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
        else if (e.Reason == SessionSwitchReason.SessionUnlock)
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

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
        {
            _logger.LogInformation("System Suspending.");
            _windowTracker.CloseCurrentSession(DateTimeOffset.UtcNow);
        }
        else if (e.Mode == PowerModes.Resume)
        {
            _logger.LogInformation("System Resumed.");
            _windowTracker.ForceReevaluate();
        }
    }
}
