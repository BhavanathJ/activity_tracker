using System;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ActivityTracker.Core.Data;
using ActivityTracker.Core.Models;
using ActivityTracker.Core.Configuration;
using ActivityTracker.Service.Tracking;

namespace ActivityTracker.Service.Http;

public class ExtensionListener
{
    private readonly ILogger _logger;
    private readonly DatabaseManager _dbManager;
    private readonly WindowSessionManager _windowSessionManager;
    private readonly TrackerConfig _config;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

    // Store ongoing browser sessions to update their end times
    // Key: browser identifier
    private readonly System.Collections.Generic.Dictionary<string, long> _currentBrowserSessions = new();

    // Store the domain currently open for each browser session (for dedup)
    private readonly System.Collections.Generic.Dictionary<string, string> _currentBrowserDomains = new();

    // Store ongoing audio sessions
    // Key: (browser, domain)
    private readonly System.Collections.Generic.Dictionary<(string, string), long> _currentAudioSessions = new();

    private readonly object _sessionLock = new();

    // Tracks the last time /audio-sync was received per browser (unix seconds).
    // Used to expire audio sessions whose browser has gone silent.
    private readonly System.Collections.Generic.Dictionary<string, long> _lastAudioSyncUnix = new();

    // Idle state from SessionAgent input-idle reports
    private bool _inputIdle;
    private bool _mediaActive;
    private bool _effectiveIdle;
    private long _lastMediaHeartbeatUnix;
    private DateTimeOffset _lastInputIdleBackdate = DateTimeOffset.UtcNow;

    // Last browser event per browser, stored when the browser's process is not foreground
    private readonly System.Collections.Generic.Dictionary<string, (string Domain, string Title, DateTimeOffset Timestamp)> _lastBrowserEvent = new(StringComparer.OrdinalIgnoreCase);

    // Lock row tracking
    private long? _lockSessionId;

    // Browser-to-process map (case-insensitive)
    private static readonly System.Collections.Generic.Dictionary<string, string[]> BrowserProcessMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { "brave", new[] { "brave" } },
        { "chrome", new[] { "chrome" } },
        { "edge", new[] { "msedge" } }
    };

    public ExtensionListener(ILogger logger, DatabaseManager dbManager, WindowSessionManager windowSessionManager)
    {
        _logger = logger;
        _dbManager = dbManager;
        _windowSessionManager = windowSessionManager;
        _config = ConfigManager.Load();
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _lastMediaHeartbeatUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _ = Task.Run(() => StartWithRetryAsync(_cts.Token));
    }

    private async Task StartWithRetryAsync(CancellationToken token)
    {
        var startTime = DateTimeOffset.UtcNow;
        var maxDuration = TimeSpan.FromMinutes(5);

        while (!token.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation($"Starting HTTP Listener on port {_config.HttpPort}...");
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://127.0.0.1:{_config.HttpPort}/");
                _listener.Start();

                _logger.LogInformation("HTTP Listener started successfully on port {Port}.", _config.HttpPort);
                _ = Task.Run(() => ListenAsync(token), token);
                _ = Task.Run(() => ExpireStaleAudioSessionsLoopAsync(token), token);
                _ = Task.Run(() => ExpireMediaLoopAsync(token), token);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start HTTP Listener on port {Port}. Retrying in 5 seconds...", _config.HttpPort);
                try { _listener?.Close(); } catch { }
                _listener = null;

                if (DateTimeOffset.UtcNow - startTime >= maxDuration)
                {
                    _logger.LogCritical("HTTP Listener failed to start after 5 minutes of retries. Exiting process with code 1 for SCM recovery.");
                    Environment.Exit(1);
                    return;
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();

        // Close all browser and audio sessions up to now
        CloseAllBrowserAndAudioSessions(DateTimeOffset.UtcNow);

        _logger.LogInformation("HTTP Listener stopped.");
    }

    /// <summary>
    /// Closes every open browser (focus) session, setting end_time to <paramref name="endTime"/>.
    /// Audio sessions are left open intentionally (background playback continues while idle/locked).
    /// Safe to call from any thread; acquires _sessionLock internally.
    /// </summary>
    public void CloseAllBrowserSessions(DateTimeOffset endTime)
    {
        var endUnix = endTime.ToUnixTimeSeconds();
        lock (_sessionLock)
        {
            foreach (var sessionId in _currentBrowserSessions.Values)
            {
                _dbManager.UpdateEventEndTime(sessionId, endUnix);
            }
            _currentBrowserSessions.Clear();
            _currentBrowserDomains.Clear();
        }
    }

    /// <summary>
    /// Closes every open audio session, setting end_time to <paramref name="endTime"/>.
    /// Safe to call from any thread; acquires _sessionLock internally.
    /// </summary>
    public void CloseAllAudioSessions(DateTimeOffset endTime)
    {
        var endUnix = endTime.ToUnixTimeSeconds();
        lock (_sessionLock)
        {
            foreach (var sessionId in _currentAudioSessions.Values)
            {
                _dbManager.UpdateEventEndTime(sessionId, endUnix);
            }
            _currentAudioSessions.Clear();
        }
    }

    /// <summary>
    /// Closes every open browser and audio session. Use on suspend or shutdown.
    /// </summary>
    public void CloseAllBrowserAndAudioSessions(DateTimeOffset endTime)
    {
        CloseAllBrowserSessions(endTime);
        CloseAllAudioSessions(endTime);
    }

    /// <summary>
    /// Closes the window session, all browser sessions, all audio sessions,
    /// and the open lock row. Called on service shutdown.
    /// </summary>
    public void CloseEverything(DateTimeOffset now)
    {
        _windowSessionManager.CloseCurrentSession(now);
        CloseAllBrowserAndAudioSessions(now);

        // Close open lock row
        lock (_sessionLock)
        {
            if (_lockSessionId.HasValue)
            {
                _dbManager.UpdateEventEndTime(_lockSessionId.Value, now.ToUnixTimeSeconds());
                _lockSessionId = null;
            }
        }
    }

    /// <summary>
    /// Gets or sets the lock session id. Used by SystemStateTracker.
    /// </summary>
    public long? LockSessionId
    {
        get { lock (_sessionLock) { return _lockSessionId; } }
        set { lock (_sessionLock) { _lockSessionId = value; } }
    }

    /// <summary>
    /// Runs every 30 s. Closes audio sessions for any browser whose last /audio-sync
    /// was received more than 90 s ago (browser dead / SW unloaded), using lastSyncUnix
    /// as the end_time so the session ends when the browser was last heard from.
    /// </summary>
    private async Task ExpireStaleAudioSessionsLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(30), token); }
            catch (OperationCanceledException) { break; }

            var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            const long StaleThresholdSeconds = 90;

            lock (_sessionLock)
            {
                var staleBrowsers = new System.Collections.Generic.List<string>();
                foreach (var kv in _lastAudioSyncUnix)
                {
                    if (nowUnix - kv.Value > StaleThresholdSeconds)
                        staleBrowsers.Add(kv.Key);
                }

                foreach (var browser in staleBrowsers)
                {
                    var lastSync = _lastAudioSyncUnix[browser];
                    var toClose = new System.Collections.Generic.List<(string, string)>();
                    foreach (var key in _currentAudioSessions.Keys)
                    {
                        if (key.Item1 == browser)
                            toClose.Add(key);
                    }
                    if (toClose.Count > 0)
                    {
                        _logger.LogWarning(
                            "Browser '{Browser}' has not sent audio-sync in {Age}s — closing {Count} stale audio session(s).",
                            browser, nowUnix - lastSync, toClose.Count);
                        foreach (var key in toClose)
                        {
                            _dbManager.UpdateEventEndTime(_currentAudioSessions[key], lastSync);
                            _currentAudioSessions.Remove(key);
                        }
                    }
                    _lastAudioSyncUnix.Remove(browser);
                }
            }
        }
    }

    private async Task ListenAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _listener != null && _listener.IsListening)
        {
            try
            {
                var context = await _listener.GetContextAsync().WaitAsync(token);
                _ = Task.Run(() => HandleRequestAsync(context), token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                    _logger.LogError(ex, "HTTP Listener error");
            }
        }
    }

    private static async Task<(string? Body, bool TooLarge)> ReadBodyWithLimitAsync(HttpListenerRequest request, int maxBytes)
    {
        if (request.ContentLength64 > maxBytes)
        {
            return (null, true);
        }

        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        int totalRead = 0;
        int read;

        while ((read = await request.InputStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            totalRead += read;
            if (totalRead > maxBytes)
            {
                return (null, true);
            }
            ms.Write(buffer, 0, read);
        }

        var text = System.Text.Encoding.UTF8.GetString(ms.ToArray());
        return (text, false);
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        try
        {
            // 1. Origin header check (HTTP hardening)
            var origin = context.Request.Headers["Origin"];
            if (!string.IsNullOrEmpty(origin))
            {
                if (!origin.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Forbidden request with unauthorized Origin: {Origin}", origin);
                    context.Response.StatusCode = 403;
                    return;
                }
                context.Response.AppendHeader("Access-Control-Allow-Origin", origin);
            }

            context.Response.AppendHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            context.Response.AppendHeader("Access-Control-Allow-Headers", "Content-Type");

            if (context.Request.HttpMethod == "OPTIONS")
            {
                context.Response.StatusCode = 200;
                return;
            }

            if (context.Request.Url!.AbsolutePath == "/config" && context.Request.HttpMethod == "GET")
            {
                context.Response.ContentType = "application/json";
                var configJson = JsonSerializer.Serialize(_config, ServiceJsonContext.Default.TrackerConfig);
                using var writer = new StreamWriter(context.Response.OutputStream);
                await writer.WriteAsync(configJson);
                return;
            }

            // 2. Cap request body at 64 KB (65536 bytes) for all POST requests
            string body = "";
            if (context.Request.HttpMethod == "POST")
            {
                var (b, tooLarge) = await ReadBodyWithLimitAsync(context.Request, 65536);
                if (tooLarge)
                {
                    _logger.LogWarning("Request body exceeded 64 KB limit on {Path}. Replying 413.", context.Request.Url?.AbsolutePath);
                    context.Response.StatusCode = 413;
                    return;
                }
                body = b ?? "";
            }

            if (context.Request.Url!.AbsolutePath == "/event" && context.Request.HttpMethod == "POST")
            {
                var payload = JsonSerializer.Deserialize(body, ServiceJsonContext.Default.ExtensionEventPayload);
                if (payload != null)
                {
                    HandleBrowserEvent(payload);
                }

                context.Response.StatusCode = 200;
                return;
            }

            if (context.Request.Url!.AbsolutePath == "/idle" && context.Request.HttpMethod == "POST")
            {
                var payload = JsonSerializer.Deserialize(body, ServiceJsonContext.Default.IdleEventPayload);
                if (payload != null)
                {
                    HandleIdleEvent(payload);
                }

                context.Response.StatusCode = 200;
                return;
            }

            if (context.Request.Url!.AbsolutePath == "/media" && context.Request.HttpMethod == "POST")
            {
                var payload = JsonSerializer.Deserialize(body, ServiceJsonContext.Default.MediaEventPayload);
                if (payload != null)
                {
                    HandleMediaEvent(payload);
                }

                context.Response.StatusCode = 200;
                return;
            }

            // Endpoint to blur a specific browser (close its active session)
            if (context.Request.Url!.AbsolutePath == "/blur" && context.Request.HttpMethod == "POST")
            {
                var payload = JsonSerializer.Deserialize(body, ServiceJsonContext.Default.BlurEventPayload);
                if (payload != null && !string.IsNullOrEmpty(payload.Browser))
                {
                    var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    lock (_sessionLock)
                    {
                        if (_currentBrowserSessions.TryGetValue(payload.Browser, out var sessId))
                        {
                            _dbManager.UpdateEventEndTime(sessId, nowUnix);
                            _currentBrowserSessions.Remove(payload.Browser);
                            _currentBrowserDomains.Remove(payload.Browser);
                        }
                    }
                }
                context.Response.StatusCode = 200;
                return;
            }

            if (context.Request.Url!.AbsolutePath == "/window" && context.Request.HttpMethod == "POST")
            {
                var payload = JsonSerializer.Deserialize(body, ServiceJsonContext.Default.WindowEventPayload);
                if (payload != null)
                {
                    _windowSessionManager.HandleWindowChange(payload.ProcessOrDomain, payload.Title);
                    HandleWindowProcessChange(payload.ProcessOrDomain);
                }

                context.Response.StatusCode = 200;
                return;
            }

            if (context.Request.Url!.AbsolutePath == "/audio-sync" && context.Request.HttpMethod == "POST")
            {
                var payload = JsonSerializer.Deserialize(body, ServiceJsonContext.Default.AudioSyncPayload);
                if (payload != null && !string.IsNullOrEmpty(payload.Browser))
                {
                    HandleAudioSync(payload);
                }

                context.Response.StatusCode = 200;
                return;
            }

            context.Response.StatusCode = 404;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling HTTP request");
            context.Response.StatusCode = 500;
        }
        finally
        {
            context.Response.Close();
        }
    }

    /// <summary>
    /// When /window reports a process change, check if it belongs to a browser
    /// and replay stored browser events, or close browser sessions if it's not a browser.
    /// </summary>
    private void HandleWindowProcessChange(string processName)
    {
        var normalizedProcess = processName.ToLowerInvariant();
        string? matchedBrowser = null;

        foreach (var kv in BrowserProcessMap)
        {
            foreach (var proc in kv.Value)
            {
                if (string.Equals(proc, normalizedProcess, StringComparison.OrdinalIgnoreCase))
                {
                    matchedBrowser = kv.Key;
                    break;
                }
            }
            if (matchedBrowser != null) break;
        }

        var now = DateTimeOffset.UtcNow;

        if (matchedBrowser != null)
        {
            // Process belongs to a browser — check if we have a stored event for it
            lock (_sessionLock)
            {
                if (!_currentBrowserSessions.ContainsKey(matchedBrowser) &&
                    _lastBrowserEvent.TryGetValue(matchedBrowser, out var stored))
                {
                    // Only replay if the stored event is less than 5 seconds old
                    if ((now - stored.Timestamp).TotalSeconds < 5)
                    {
                        _lastBrowserEvent.Remove(matchedBrowser);
                        OpenBrowserSessionLocked(matchedBrowser, stored.Domain, stored.Title, now.ToUnixTimeSeconds());
                    }
                }
            }
        }
        else
        {
            // Not a browser process — close all browser sessions
            CloseAllBrowserSessions(now);
        }
    }

    private void HandleBrowserEvent(ExtensionEventPayload payload)
    {
        if (string.IsNullOrEmpty(payload.Browser) || string.IsNullOrEmpty(payload.Url)) return;

        var domain = GetDomainFromUrl(payload.Url);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // If the domain is ignored, still close the previous browser session before returning.
        if (ShouldIgnoreDomain(domain))
        {
            if (!payload.IsAudioOnly)
            {
                lock (_sessionLock)
                {
                    if (_currentBrowserSessions.TryGetValue(payload.Browser, out var prevId))
                    {
                        _dbManager.UpdateEventEndTime(prevId, now);
                        _currentBrowserSessions.Remove(payload.Browser);
                        _currentBrowserDomains.Remove(payload.Browser);
                    }
                }
            }
            return;
        }

        lock (_sessionLock)
        {
            // Focus event: open a browser session ONLY if the current foreground process
            // belongs to this browser. Otherwise store the event for replay.
            if (!payload.IsAudioOnly)
            {
                var currentProcess = _windowSessionManager.CurrentProcess;
                if (!IsProcessForBrowser(payload.Browser, currentProcess))
                {
                    // Store for replay when the browser's process becomes foreground
                    _lastBrowserEvent[payload.Browser] = (domain, payload.Title ?? "", DateTimeOffset.UtcNow);
                    return;
                }

                // Dedupe: if domain equals the currently open session, do nothing
                if (_currentBrowserDomains.TryGetValue(payload.Browser, out var currentDomain) &&
                    string.Equals(currentDomain, domain, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                // Close previous session for this browser
                if (_currentBrowserSessions.TryGetValue(payload.Browser, out var prevSessionId))
                {
                    _dbManager.UpdateEventEndTime(prevSessionId, now);
                }

                OpenBrowserSessionLocked(payload.Browser, domain, payload.Title ?? "", now);
            }

            // Audio event (active tab or background tab).
            if (payload.IsAudioOnly)
            {
                var key = (payload.Browser, domain);
                if (payload.Audible && !payload.IsAudioStop)
                {
                    // If an audio session already exists for this (browser, domain), close it first.
                    if (_currentAudioSessions.TryGetValue(key, out var existingId))
                    {
                        _dbManager.UpdateEventEndTime(existingId, now);
                        _currentAudioSessions.Remove(key);
                    }
                    var audioRecord = new EventRecord
                    {
                        Type = "audio",
                        ProcessOrDomain = domain,
                        Browser = payload.Browser,
                        Title = payload.Title ?? "",
                        StartTime = now
                    };
                    _currentAudioSessions[key] = _dbManager.InsertEvent(audioRecord);
                }
                else if (payload.IsAudioStop)
                {
                    if (_currentAudioSessions.TryGetValue(key, out var sessionId))
                    {
                        _dbManager.UpdateEventEndTime(sessionId, now);
                        _currentAudioSessions.Remove(key);
                    }
                }
            }
        }
    }

    private void HandleIdleEvent(IdleEventPayload payload)
    {
        if (payload.State == "idle")
        {
            _logger.LogInformation($"SessionAgent reports idle ({payload.IdleSeconds:F0}s).");
            lock (_sessionLock)
            {
                _inputIdle = true;
                _lastInputIdleBackdate = DateTimeOffset.UtcNow.AddSeconds(-payload.IdleSeconds);
                ApplyEffectiveIdle(fromMediaChange: false, backdatedTime: _lastInputIdleBackdate);
            }
        }
        else if (payload.State == "active")
        {
            _logger.LogInformation("SessionAgent reports user active.");
            lock (_sessionLock)
            {
                _inputIdle = false;
                ApplyEffectiveIdle(fromMediaChange: false);
            }
        }
    }

    private void HandleMediaEvent(MediaEventPayload payload)
    {
        lock (_sessionLock)
        {
            _lastMediaHeartbeatUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            bool newMediaActive = payload.Playing;

            if (_mediaActive != newMediaActive)
            {
                _logger.LogInformation("Media state changed: _mediaActive={Playing} (Source={Source}, Process={Process})",
                    newMediaActive, payload.Source, payload.Process);
                _mediaActive = newMediaActive;
                ApplyEffectiveIdle(fromMediaChange: true);
            }
        }
    }

    /// <summary>
    /// Applies the effective idle state: effectiveIdle = _inputIdle && !_mediaActive.
    /// When effectiveIdle turns true:
    /// - If triggered by input idle (no media): close backdated to last input time.
    /// - If triggered by media stopping while input is already idle: close NOW (time media stopped).
    /// When effectiveIdle turns false (either user active or media started):
    /// - Treat as active (SetIdle(false)) and reopen from the remembered window.
    /// Must be called under _sessionLock.
    /// </summary>
    private void ApplyEffectiveIdle(bool fromMediaChange, DateTimeOffset? backdatedTime = null)
    {
        bool newEffectiveIdle = _inputIdle && !_mediaActive;

        if (!_effectiveIdle && newEffectiveIdle)
        {
            _effectiveIdle = true;
            _windowSessionManager.SetIdle(true);

            DateTimeOffset closeTime;
            if (fromMediaChange)
            {
                // _mediaActive turned false while _inputIdle was true: close NOW
                closeTime = DateTimeOffset.UtcNow;
                _logger.LogInformation("Media stopped while input idle. Closing sessions NOW ({Time}).", closeTime.ToString("o"));
            }
            else
            {
                // Input idle with no media: close backdated to last input time
                closeTime = backdatedTime ?? _lastInputIdleBackdate;
                _logger.LogInformation("Entering effective idle. Closing sessions backdated to {Time}.", closeTime.ToString("o"));
            }

            _windowSessionManager.CloseCurrentSession(closeTime);
            CloseAllBrowserSessions(closeTime);
        }
        else if (_effectiveIdle && !newEffectiveIdle)
        {
            _effectiveIdle = false;
            _logger.LogInformation("Leaving effective idle (_inputIdle={InputIdle}, _mediaActive={MediaActive}). Treating as active.",
                _inputIdle, _mediaActive);

            _windowSessionManager.SetIdle(false);
            _windowSessionManager.ReopenRememberedWindow();
        }
    }

    /// <summary>
    /// Auto-expires _mediaActive if no /media heartbeat has arrived for 90 s.
    /// </summary>
    private async Task ExpireMediaLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(5), token); }
            catch (OperationCanceledException) { break; }

            lock (_sessionLock)
            {
                if (_mediaActive)
                {
                    var elapsed = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - _lastMediaHeartbeatUnix;
                    if (elapsed >= 90)
                    {
                        _logger.LogWarning("No /media heartbeat received in {Elapsed}s (threshold 90s) — expiring _mediaActive to false.", elapsed);
                        _mediaActive = false;
                        ApplyEffectiveIdle(fromMediaChange: true);
                    }
                }
            }
        }
    }

    private void HandleAudioSync(AudioSyncPayload payload)
    {
        // Build a set of the domains the browser says are currently audible.
        var activeDomains = new System.Collections.Generic.HashSet<string>(
            payload.Domains ?? System.Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        lock (_sessionLock)
        {
            // Record heartbeat time so the stale-session timer knows this browser is alive.
            _lastAudioSyncUnix[payload.Browser] = now;

            // 1. Close sessions for domains no longer audible in this browser.
            var toClose = new System.Collections.Generic.List<(string, string)>();
            foreach (var key in _currentAudioSessions.Keys)
            {
                if (key.Item1 == payload.Browser && !activeDomains.Contains(key.Item2))
                    toClose.Add(key);
            }
            foreach (var key in toClose)
            {
                _dbManager.UpdateEventEndTime(_currentAudioSessions[key], now);
                _currentAudioSessions.Remove(key);
            }

            // 2. Open sessions for domains that are audible but have no open session.
            foreach (var domain in activeDomains)
            {
                if (ShouldIgnoreDomain(domain)) continue;
                var key = (payload.Browser, domain);
                if (!_currentAudioSessions.ContainsKey(key))
                {
                    var record = new EventRecord
                    {
                        Type = "audio",
                        ProcessOrDomain = domain,
                        Browser = payload.Browser,
                        Title = "",
                        StartTime = now
                    };
                    _currentAudioSessions[key] = _dbManager.InsertEvent(record);
                }
                // Existing session: leave it open (do not close/reopen).
            }
        }
    }

    /// <summary>
    /// Opens a browser session and records the domain for dedup. Must be called under _sessionLock.
    /// </summary>
    private void OpenBrowserSessionLocked(string browser, string domain, string title, long nowUnix)
    {
        var record = new EventRecord
        {
            Type = "browser",
            ProcessOrDomain = domain,
            Browser = browser,
            Title = title,
            StartTime = nowUnix
        };

        _currentBrowserSessions[browser] = _dbManager.InsertEvent(record);
        _currentBrowserDomains[browser] = domain;
    }

    /// <summary>
    /// Returns true if the given processName (case-insensitive) is in the process list
    /// for the given browser in BrowserProcessMap.
    /// </summary>
    private static bool IsProcessForBrowser(string browser, string? processName)
    {
        if (string.IsNullOrEmpty(processName)) return false;
        if (!BrowserProcessMap.TryGetValue(browser, out var processes)) return false;
        foreach (var p in processes)
        {
            if (string.Equals(p, processName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private string GetDomainFromUrl(string urlStr)
    {
        if (Uri.TryCreate(urlStr, UriKind.Absolute, out var uri))
        {
            return uri.Host;
        }
        return urlStr;
    }

    private bool ShouldIgnoreDomain(string domain)
    {
        var lowerDomain = domain.ToLowerInvariant();
        if (_config.ExcludeDomains.Count > 0 && _config.ExcludeDomains.Exists(d => DomainMatches(lowerDomain, d.ToLowerInvariant())))
            return true;
        if (_config.IncludeDomains.Count > 0 && !_config.IncludeDomains.Exists(d => DomainMatches(lowerDomain, d.ToLowerInvariant())))
            return true;
        return false;
    }

    /// <summary>
    /// Returns true if <paramref name="domain"/> equals <paramref name="pattern"/>
    /// or is a subdomain of it (i.e. ends with "." + pattern).
    /// For example: "mail.google.com" matches "google.com", but "notgoogle.com" does not.
    /// </summary>
    private static bool DomainMatches(string domain, string pattern)
    {
        return domain == pattern || domain.EndsWith("." + pattern, StringComparison.Ordinal);
    }
}

public class ExtensionEventPayload
{
    public string Browser { get; set; } = "";
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public bool Audible { get; set; }
    public bool IsAudioOnly { get; set; } // True if this event is just an audio change (not a focus change)
    public bool IsAudioStop { get; set; } // True if audio stopped
}

public class WindowEventPayload
{
    public string ProcessOrDomain { get; set; } = "";
    public string Title { get; set; } = "";
}

public class BlurEventPayload
{
    public string Browser { get; set; } = "";
}

public class AudioSyncPayload
{
    public string Browser { get; set; } = "";
    public string[] Domains { get; set; } = System.Array.Empty<string>();
}
