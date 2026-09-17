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

namespace ActivityTracker.Service.Http;

public class ExtensionListener
{
    private readonly ILogger _logger;
    private readonly DatabaseManager _dbManager;
    private readonly TrackerConfig _config;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    
    // Store ongoing browser sessions to update their end times
    // Key: browser identifier
    private readonly System.Collections.Generic.Dictionary<string, long> _currentBrowserSessions = new();

    public ExtensionListener(ILogger logger, DatabaseManager dbManager)
    {
        _logger = logger;
        _dbManager = dbManager;
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
        foreach (var sessionId in _currentBrowserSessions.Values)
        {
            _dbManager.UpdateEventEndTime(sessionId, now);
        }
        _currentBrowserSessions.Clear();
        
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
                var configJson = JsonSerializer.Serialize(_config);
                using var writer = new StreamWriter(context.Response.OutputStream);
                await writer.WriteAsync(configJson);
                return;
            }

            if (context.Request.Url!.AbsolutePath == "/event" && context.Request.HttpMethod == "POST")
            {
                using var reader = new StreamReader(context.Request.InputStream);
                var body = await reader.ReadToEndAsync();
                
                var payload = JsonSerializer.Deserialize<ExtensionEventPayload>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (payload != null)
                {
                    HandleBrowserEvent(payload);
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
        if (payload.Audible && payload.IsAudioOnly)
        {
            // Only log audio if it's an audio-specific event (background tab).
            // If it's the active tab and it's audible, it's already covered by the browser focus event above,
            // or the extension handles sending an "audio only" flag when a background tab starts playing.
            var audioRecord = new EventRecord
            {
                Type = "audio",
                ProcessOrDomain = domain,
                Browser = payload.Browser,
                Title = payload.Title ?? "",
                StartTime = now
            };
            // Note: We don't track audio session ends robustly yet, we'll need the extension to send an end event
            // or we just log points in time. The spec says "Audio events: log only when a tab is audible".
            // Let's assume the extension sends an event when audio starts, and another when it stops.
            // For now, insert it and we'll handle the end time logic by keeping track of active audio per browser/tab.
            
            // To simplify, let's just insert it. If the extension sends an "AudioStop" we can find it and close it.
            // We'll refine this based on the extension payload.
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
        if (_config.ExcludeDomains.Count > 0 && _config.ExcludeDomains.Exists(d => lowerDomain.Contains(d.ToLowerInvariant())))
            return true;
        if (_config.IncludeDomains.Count > 0 && !_config.IncludeDomains.Exists(d => lowerDomain.Contains(d.ToLowerInvariant())))
            return true;
        return false;
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
