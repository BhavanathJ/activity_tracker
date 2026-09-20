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
    
    // Store ongoing audio sessions
    // Key: (browser, domain)
    private readonly System.Collections.Generic.Dictionary<(string, string), long> _currentAudioSessions = new();

    private readonly object _sessionLock = new();

    public ExtensionListener(ILogger logger, DatabaseManager dbManager, WindowSessionManager windowSessionManager)
    {
        _logger = logger;
        _dbManager = dbManager;
        _windowSessionManager = windowSessionManager;
        _config = ConfigManager.Load();
    }

    public void Start()
    {
        _logger.LogInformation($"Starting HTTP Listener on port {_config.HttpPort}...");
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{_config.HttpPort}/");
        
        try
        {
            _listener.Start();
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => ListenAsync(_cts.Token));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start HTTP Listener");
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
        
        // Close all browser sessions
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        lock (_sessionLock)
        {
            foreach (var sessionId in _currentBrowserSessions.Values)
            {
                _dbManager.UpdateEventEndTime(sessionId, now);
            }
            _currentBrowserSessions.Clear();

            foreach (var sessionId in _currentAudioSessions.Values)
            {
                _dbManager.UpdateEventEndTime(sessionId, now);
            }
            _currentAudioSessions.Clear();
        }
        
        _logger.LogInformation("HTTP Listener stopped.");
    }

    private async Task ListenAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var context = await _listener!.GetContextAsync().WaitAsync(token);
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

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        try
        {
            // Simple CORS
            context.Response.AppendHeader("Access-Control-Allow-Origin", "*");
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

            if (context.Request.Url!.AbsolutePath == "/event" && context.Request.HttpMethod == "POST")
            {
                using var reader = new StreamReader(context.Request.InputStream);
                var body = await reader.ReadToEndAsync();
                
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
                using var reader = new StreamReader(context.Request.InputStream);
                var body = await reader.ReadToEndAsync();

                var payload = JsonSerializer.Deserialize(body, ServiceJsonContext.Default.IdleEventPayload);
                if (payload != null)
                {
                    HandleIdleEvent(payload);
                }

                context.Response.StatusCode = 200;
                return;
            }

            if (context.Request.Url!.AbsolutePath == "/window" && context.Request.HttpMethod == "POST")
            {
                using var reader = new StreamReader(context.Request.InputStream);
                var body = await reader.ReadToEndAsync();

                var payload = JsonSerializer.Deserialize(body, ServiceJsonContext.Default.WindowEventPayload);
                if (payload != null)
                {
                    _windowSessionManager.HandleWindowChange(payload.ProcessOrDomain, payload.Title);
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

    private void HandleBrowserEvent(ExtensionEventPayload payload)
    {
        if (string.IsNullOrEmpty(payload.Browser) || string.IsNullOrEmpty(payload.Url)) return;
        
        var domain = GetDomainFromUrl(payload.Url);
        if (ShouldIgnoreDomain(domain)) return;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        lock (_sessionLock)
        {
            // If it's a focus event, close the previous session for this browser
            if (!payload.IsAudioOnly)
            {
                if (_currentBrowserSessions.TryGetValue(payload.Browser, out var prevSessionId))
                {
                    _dbManager.UpdateEventEndTime(prevSessionId, now);
                }

                var record = new EventRecord
                {
                    Type = "browser",
                    ProcessOrDomain = domain,
                    Browser = payload.Browser,
                    Title = payload.Title ?? "",
                    StartTime = now
                };
                
                _currentBrowserSessions[payload.Browser] = _dbManager.InsertEvent(record);
            }
            
            // Handle Audio
            if (payload.IsAudioOnly)
            {
                var key = (payload.Browser, domain);
                if (payload.Audible && !payload.IsAudioStop)
                {
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
            // Backdate the session end to when input actually stopped
            var endTime = DateTimeOffset.UtcNow.AddSeconds(-payload.IdleSeconds);
            _windowSessionManager.CloseCurrentSession(endTime);
        }
        else if (payload.State == "active")
        {
            _logger.LogInformation("SessionAgent reports user active.");
            _windowSessionManager.ForceReevaluate();
        }
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
    public bool IsAudioOnly { get; set; } // True if this event is just a background audio change
    public bool IsAudioStop { get; set; } // True if audio stopped
}

public class WindowEventPayload
{
    public string ProcessOrDomain { get; set; } = "";
    public string Title { get; set; } = "";
}
