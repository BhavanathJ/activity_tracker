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
    private IntPtr _wtsSessionHandle = IntPtr.Zero;

    private const int NOTIFY_FOR_THIS_SESSION = 0;
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
        
        var hwnd = _windowTracker.GetMessageWindowHandle();
        if (hwnd != IntPtr.Zero)
        {
            if (WTSRegisterSessionNotification(hwnd, NOTIFY_FOR_THIS_SESSION))
            {
                _wtsSessionHandle = hwnd;
                _logger.LogInformation("WTS Session Notification registered successfully.");
            }
            else
            {
                _logger.LogWarning("Failed to register for WTS Session Notification.");
            }

            _windowTracker.OnWtsSessionChange = HandleWtsSessionChange;
            _windowTracker.OnPowerBroadcast = HandlePowerBroadcast;
        }
        else
        {
            _logger.LogWarning("Could not register SystemStateTracker: WindowTracker provided no HWND.");
        }
    }

    public void Stop()
    {
        if (_wtsSessionHandle != IntPtr.Zero)
        {
            WTSUnRegisterSessionNotification(_wtsSessionHandle);
            _wtsSessionHandle = IntPtr.Zero;
        }

        if (_windowTracker != null)
        {
            _windowTracker.OnWtsSessionChange = null;
            _windowTracker.OnPowerBroadcast = null;
        }

        _logger.LogInformation("System State Tracker stopped.");
    }

    private void HandleWtsSessionChange(IntPtr wParam)
    {
        var now = DateTimeOffset.UtcNow;
        int reason = wParam.ToInt32();
        
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

    private void HandlePowerBroadcast(IntPtr wParam)
    {
        int eventType = wParam.ToInt32();
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

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSRegisterSessionNotification(IntPtr hWnd, int dwFlags);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);
}
