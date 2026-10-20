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

    // Current open session identity for dedup
    private string? _currentProcess;
    private string? _currentTitle;

    // Suppression flags
    private bool _idle;
    private bool _locked;

    // Remembered window while suppressed or active
    private string? _pendingProcess;
    private string? _pendingTitle;
    private string? _lastKnownProcess;
    private string? _lastKnownTitle;

    /// <summary>
    /// The process name of the currently open window session (null if none).
    /// </summary>
    public string? CurrentProcess
    {
        get { lock (_sessionLock) { return _currentProcess; } }
    }

    public WindowSessionManager(ILogger logger, DatabaseManager dbManager)
    {
        _logger = logger;
        _dbManager = dbManager;
    }

    public void SetIdle(bool idle)
    {
        lock (_sessionLock)
        {
            _idle = idle;
            if (!_idle && !_locked)
            {
                ResumeFromSuppression();
            }
        }
    }

    public void SetLocked(bool locked)
    {
        lock (_sessionLock)
        {
            _locked = locked;
            if (!_idle && !_locked)
            {
                ResumeFromSuppression();
            }
        }
    }

    public void HandleWindowChange(string processName, string title)
    {
        lock (_sessionLock)
        {
            _lastKnownProcess = processName;
            _lastKnownTitle = title;

            if (_idle || _locked)
            {
                // Suppressed: just remember the latest window, do not open a session.
                _pendingProcess = processName;
                _pendingTitle = title;
                _logger.LogDebug("Window change suppressed (idle={Idle}, locked={Locked}): process={Process}",
                    _idle, _locked, processName);
                return;
            }

            // Dedupe: if the new window is the same as the current session, do nothing.
            if (_currentSessionId.HasValue &&
                string.Equals(_currentProcess, processName, StringComparison.Ordinal) &&
                string.Equals(_currentTitle, title, StringComparison.Ordinal))
            {
                return;
            }

            CloseCurrentSessionLocked();
            OpenSessionLocked(processName, title);
        }
    }

    public void CloseCurrentSession(DateTimeOffset? endTime = null)
    {
        lock (_sessionLock)
        {
            CloseCurrentSessionLocked(endTime);
        }
    }

    /// <summary>
    /// Reopens a window session from the remembered window if not currently suppressed.
    /// Exposes a cheap way to re-trigger reopening when media becomes active while idle.
    /// </summary>
    public void ReopenRememberedWindow()
    {
        lock (_sessionLock)
        {
            if (!_idle && !_locked)
            {
                ResumeFromSuppression();
            }
        }
    }

    public void ForceReevaluate()
    {
        // No-op. The service can no longer get the foreground window directly.
        // It relies on the SessionAgent to send a new window event, which naturally
        // happens as the user interacts with the system after unlock/resume.
        _logger.LogDebug("ForceReevaluate called. Waiting for next window event from SessionAgent.");
    }

    /// <summary>
    /// Called when suppression is lifted (both _idle and _locked are false).
    /// Opens a session for the remembered window if we have one.
    /// </summary>
    private void ResumeFromSuppression()
    {
        // Must be called under _sessionLock
        var process = _pendingProcess ?? _lastKnownProcess;
        var title = _pendingTitle ?? _lastKnownTitle;

        if (process != null && title != null)
        {
            // Dedupe: if same as current session, just clear pending
            if (_currentSessionId.HasValue &&
                string.Equals(_currentProcess, process, StringComparison.Ordinal) &&
                string.Equals(_currentTitle, title, StringComparison.Ordinal))
            {
                _pendingProcess = null;
                _pendingTitle = null;
                return;
            }

            CloseCurrentSessionLocked();
            OpenSessionLocked(process, title);
            _pendingProcess = null;
            _pendingTitle = null;
        }
    }

    private void OpenSessionLocked(string processName, string title)
    {
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
        _currentProcess = processName;
        _currentTitle = title;
        _lastKnownProcess = processName;
        _lastKnownTitle = title;

        // Never log window titles — log process name only.
        _logger.LogInformation("Foreground window session started: {Process}", processName);
    }

    private void CloseCurrentSessionLocked(DateTimeOffset? endTime = null)
    {
        if (_currentSessionId.HasValue)
        {
            var end = endTime ?? DateTimeOffset.UtcNow;

            if (end < _currentSessionStartTime)
            {
                _logger.LogWarning(
                    "Negative-duration session detected. Clamping end time to start time. " +
                    "SessionId={SessionId}, StartTime={StartTime}, EndTime={EndTime}",
                    _currentSessionId.Value,
                    _currentSessionStartTime.ToString("o"),
                    end.ToString("o"));
                end = _currentSessionStartTime;
            }

            _dbManager.UpdateEventEndTime(_currentSessionId.Value, end.ToUnixTimeSeconds());
            _currentSessionId = null;
            _currentProcess = null;
            _currentTitle = null;
        }
    }
}
