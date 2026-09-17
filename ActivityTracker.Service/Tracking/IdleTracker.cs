using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Logging;
using ActivityTracker.Core.Configuration;

namespace ActivityTracker.Service.Tracking;

public partial class IdleTracker
{
    private readonly ILogger _logger;
    private readonly WindowTracker _windowTracker;
    private readonly TrackerConfig _config;
    private Timer? _timer;
    private bool _isIdle = false;

    public IdleTracker(ILogger logger, WindowTracker windowTracker)
    {
        _logger = logger;
        _windowTracker = windowTracker;
        _config = ConfigManager.Load();
    }

    public void Start()
    {
        _logger.LogInformation("Starting Idle Tracker...");
        _timer = new Timer(CheckIdle, null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    public void Stop()
    {
        _timer?.Dispose();
        _logger.LogInformation("Idle Tracker stopped.");
    }

    private void CheckIdle(object? state)
    {
        var info = new LASTINPUTINFO();
        info.cbSize = (uint)Marshal.SizeOf(info);
        
        if (GetLastInputInfo(ref info))
        {
            uint currentTicks = (uint)Environment.TickCount;
            uint idleTicks = currentTicks - info.dwTime;
            double idleSeconds = idleTicks / 1000.0;

            if (idleSeconds >= _config.IdleTimeoutSeconds)
            {
                if (!_isIdle)
                {
                    _logger.LogInformation($"System went idle (>{_config.IdleTimeoutSeconds}s). Pausing tracking.");
                    _isIdle = true;
                    // Calculate exact time when input stopped
                    var endTime = DateTimeOffset.UtcNow.AddSeconds(-idleSeconds);
                    _windowTracker.CloseCurrentSession(endTime);
                }
            }
            else
            {
                if (_isIdle)
                {
                    _logger.LogInformation("System active again. Resuming tracking.");
                    _isIdle = false;
                    _windowTracker.ForceReevaluate();
                }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
}
