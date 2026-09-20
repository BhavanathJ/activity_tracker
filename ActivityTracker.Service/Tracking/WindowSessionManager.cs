using System;
using Microsoft.Extensions.Logging;
using ActivityTracker.Core.Data;
using ActivityTracker.Core.Models;

namespace ActivityTracker.Service.Tracking;

public class WindowSessionManager
{
    private readonly ILogger _logger;
    private readonly DatabaseManager _dbManager;
    private readonly object _sessionLock = new();
    
    private long? _currentSessionId;
    private DateTimeOffset _currentSessionStartTime;

    public WindowSessionManager(ILogger logger, DatabaseManager dbManager)
    {
        _logger = logger;
        _dbManager = dbManager;
    }

    public void HandleWindowChange(string processName, string title)
    {
        lock (_sessionLock)
        {
            CloseCurrentSessionLocked();

            var now = DateTimeOffset.UtcNow;
            var record = new EventRecord
            {
                Type = "window",
                ProcessOrDomain = processName,
                Title = title,
                StartTime = now.ToUnixTimeSeconds()
            };

            _currentSessionId = _dbManager.InsertEvent(record);
            _currentSessionStartTime = now;

            _logger.LogInformation($"Foreground window session started: {processName} - {title}");
        }
    }

    public void CloseCurrentSession(DateTimeOffset? endTime = null)
    {
        lock (_sessionLock)
        {
            CloseCurrentSessionLocked(endTime);
        }
    }

    public void ForceReevaluate()
    {
        // No-op. The service can no longer get the foreground window directly.
        // It relies on the SessionAgent to send a new window event, which naturally
        // happens as the user interacts with the system after unlock/resume.
        _logger.LogDebug("ForceReevaluate called. Waiting for next window event from SessionAgent.");
    }

    private void CloseCurrentSessionLocked(DateTimeOffset? endTime = null)
    {
        if (_currentSessionId.HasValue)
        {
            var end = endTime ?? DateTimeOffset.UtcNow;

            if (end < _currentSessionStartTime)
            {
                _logger.LogWarning(
                    "Negative-duration session detected and discarded. " +
                    "SessionId={SessionId}, StartTime={StartTime}, EndTime={EndTime}",
                    _currentSessionId.Value,
                    _currentSessionStartTime.ToString("o"),
                    end.ToString("o"));
                _currentSessionId = null;
                return;
            }

            _dbManager.UpdateEventEndTime(_currentSessionId.Value, end.ToUnixTimeSeconds());
            _currentSessionId = null;
        }
    }
}
