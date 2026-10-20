using System;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ActivityTracker.SessionAgent;

public class IdleMonitor
{
    private readonly int _idleTimeoutSeconds;
    private readonly string _serviceUrl;
    private readonly HttpClient _httpClient;
    private readonly WindowTracker _windowTracker;
    private Timer? _timer;
    private bool _isIdle = false;
    private DateTimeOffset _lastPostTime = DateTimeOffset.UtcNow;
    private readonly object _pollLock = new();

    public IdleMonitor(int idleTimeoutSeconds, int httpPort, WindowTracker windowTracker)
    {
        _idleTimeoutSeconds = idleTimeoutSeconds;
        _serviceUrl = $"http://127.0.0.1:{httpPort}/idle";
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        _windowTracker = windowTracker;
    }

    public void Start()
    {
        FileLogger.LogInfo($"Idle monitor started (timeout={_idleTimeoutSeconds}s, heartbeat=30s).");
        _lastPostTime = DateTimeOffset.UtcNow;
        _timer = new Timer(CheckIdle, null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    public void Stop()
    {
        _timer?.Dispose();
        _httpClient.Dispose();
        FileLogger.LogInfo("Idle monitor stopped.");
    }

    private void CheckIdle(object? state)
    {
        if (!Monitor.TryEnter(_pollLock))
            return;

        try
        {
            var info = new LASTINPUTINFO();
            info.cbSize = (uint)Marshal.SizeOf(info);

            if (GetLastInputInfo(ref info))
            {
                uint currentTicks = (uint)Environment.TickCount;
                uint idleTicks = currentTicks - info.dwTime;
                double idleSeconds = idleTicks / 1000.0;
                var now = DateTimeOffset.UtcNow;

                if (idleSeconds >= _idleTimeoutSeconds)
                {
                    if (!_isIdle)
                    {
                        FileLogger.LogInfo($"Idle detected ({idleSeconds:F0}s >= {_idleTimeoutSeconds}s).");
                        // Only set _isIdle after a successful POST
                        if (PostIdleState("idle", idleSeconds))
                        {
                            _isIdle = true;
                            _lastPostTime = now;
                        }
                        // On failure, retry on the next 5 s tick (don't set _isIdle)
                    }
                    else if ((now - _lastPostTime) >= TimeSpan.FromSeconds(30))
                    {
                        if (PostIdleState("idle", idleSeconds))
                        {
                            _lastPostTime = now;
                        }
                    }
                }
                else
                {
                    if (_isIdle)
                    {
                        FileLogger.LogInfo("User active again.");
                        // Only set _isIdle after a successful POST
                        if (PostIdleState("active", 0))
                        {
                            _isIdle = false;
                            _lastPostTime = now;
                            // After successful POST of "active", force-report the current window
                            _windowTracker.ForceReport();
                        }
                        // On failure, retry on the next 5 s tick (remain _isIdle = true)
                    }
                    else if ((now - _lastPostTime) >= TimeSpan.FromSeconds(30))
                    {
                        if (PostIdleState("active", 0))
                        {
                            _lastPostTime = now;
                        }
                    }
                }
            }
        }
        finally
        {
            Monitor.Exit(_pollLock);
        }
    }

    /// <summary>
    /// Posts the idle state to the service. Returns true on success, false on failure.
    /// </summary>
    private bool PostIdleState(string idleState, double idleSeconds)
    {
        try
        {
            var json = $"{{\"state\":\"{idleState}\",\"idleSeconds\":{idleSeconds:F1}}}";
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = _httpClient.PostAsync(_serviceUrl, content).GetAwaiter().GetResult();
            FileLogger.LogInfo($"POST /idle ({idleState}) -> {(int)response.StatusCode}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            // Service may not be running yet — silently retry on next cycle
            FileLogger.LogWarn($"POST /idle failed: {ex.Message}");
            return false;
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
