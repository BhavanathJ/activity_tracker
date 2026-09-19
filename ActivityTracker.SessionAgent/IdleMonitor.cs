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
    private Timer? _timer;
    private bool _isIdle = false;

    public IdleMonitor(int idleTimeoutSeconds, int httpPort)
    {
        _idleTimeoutSeconds = idleTimeoutSeconds;
        _serviceUrl = $"http://127.0.0.1:{httpPort}/idle";
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    }

    public void Start()
    {
        Console.Error.WriteLine($"[SessionAgent] Idle monitor started (timeout={_idleTimeoutSeconds}s).");
        _timer = new Timer(CheckIdle, null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    public void Stop()
    {
        _timer?.Dispose();
        _httpClient.Dispose();
        Console.Error.WriteLine("[SessionAgent] Idle monitor stopped.");
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

            if (idleSeconds >= _idleTimeoutSeconds)
            {
                if (!_isIdle)
                {
                    Console.Error.WriteLine($"[SessionAgent] Idle detected ({idleSeconds:F0}s >= {_idleTimeoutSeconds}s).");
                    _isIdle = true;
                    PostIdleState("idle", idleSeconds);
                }
            }
            else
            {
                if (_isIdle)
                {
                    Console.Error.WriteLine("[SessionAgent] User active again.");
                    _isIdle = false;
                    PostIdleState("active", 0);
                }
            }
        }
    }

    private void PostIdleState(string idleState, double idleSeconds)
    {
        try
        {
            var json = $"{{\"state\":\"{idleState}\",\"idleSeconds\":{idleSeconds:F1}}}";
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = _httpClient.PostAsync(_serviceUrl, content).GetAwaiter().GetResult();
            Console.Error.WriteLine($"[SessionAgent] POST /idle ({idleState}) -> {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            // Service may not be running yet — silently retry on next cycle
            Console.Error.WriteLine($"[SessionAgent] POST /idle failed: {ex.Message}");
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
