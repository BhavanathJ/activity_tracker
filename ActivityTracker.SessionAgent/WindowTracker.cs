using System;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ActivityTracker.Core.Configuration;

namespace ActivityTracker.SessionAgent;

public class WindowTracker
{
    private readonly TrackerConfig _config;
    private readonly HttpClient _httpClient = new();
    
    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);
    private WinEventDelegate? _dele;
    private IntPtr _hook;
    private IntPtr _msgHwnd = IntPtr.Zero;

    private const uint EVENT_SYSTEM_FOREGROUND = 3;
    private const uint WINEVENT_OUTOFCONTEXT = 0;

    private const string WndClassName = "ActivityTrackerSessionAgentWnd";
    private WndProcDelegate? _wndProcDelegate;

    private string _lastReportedProcess = "";
    private string _lastReportedTitle = "";

    // Channel-based single-consumer queue for ordered delivery
    private readonly Channel<(string Process, string Title)> _sendChannel =
        Channel.CreateBounded<(string, string)>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

    private Task? _senderTask;
    private CancellationTokenSource? _cts;
    private Timer? _heartbeatTimer;

    private readonly object _debounceLock = new();
    private bool _transientIgnored;

    public WindowTracker(TrackerConfig config)
    {
        _config = config;
    }

    public void Start()
    {
        FileLogger.LogInfo("Starting Window Tracker in SessionAgent...");
        _cts = new CancellationTokenSource();
        _dele = new WinEventDelegate(WinEventProc);

        // Start the single-consumer sender task
        _senderTask = Task.Run(() => SenderLoopAsync(_cts.Token));

        var windowReady = new ManualResetEventSlim(false);

        var hookThread = new Thread(() =>
        {
            _wndProcDelegate = new WndProcDelegate(MsgWndProc);
            var wndClass = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
                hInstance = GetModuleHandle(null),
                lpszClassName = WndClassName
            };
            RegisterClassEx(ref wndClass);

            _msgHwnd = CreateWindowEx(
                0, WndClassName, "ActivityTrackerSessionAgent",
                0, 0, 0, 0, 0,
                HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

            windowReady.Set();

            _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _dele, 0, 0, WINEVENT_OUTOFCONTEXT);
            if (_hook == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                FileLogger.LogError($"SetWinEventHook FAILED. Win32 error code: {error}");
            }
            else
            {
                FileLogger.LogInfo("SetWinEventHook succeeded, hook registered.");
            }
            
            // Standard Win32 message pump
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        });
        hookThread.IsBackground = true;
        hookThread.SetApartmentState(ApartmentState.STA);
        hookThread.Start();
        
        windowReady.Wait(TimeSpan.FromSeconds(5));

        // Force log the initial foreground window
        LogForegroundWindow(GetForegroundWindow());

        // 30 s heartbeat: re-report the current foreground window (service dedupes)
        _heartbeatTimer = new Timer(HeartbeatTick, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public void Stop()
    {
        _heartbeatTimer?.Dispose();
        _cts?.Cancel();

        if (_hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
        if (_msgHwnd != IntPtr.Zero)
        {
            PostMessage(_msgHwnd, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }
        // Complete the channel so the sender loop exits
        _sendChannel.Writer.TryComplete();
        _senderTask?.Wait(TimeSpan.FromSeconds(3));
        FileLogger.LogInfo("Window Tracker stopped.");
    }

    public void ForceReport()
    {
        lock (_debounceLock)
        {
            // Clear debounce state so the current window is guaranteed to be re-reported
            _lastReportedProcess = "";
            _lastReportedTitle = "";
            _transientIgnored = false;
        }
        
        LogForegroundWindow(GetForegroundWindow());
    }

    private void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        LogForegroundWindow(hwnd);
    }

    private IntPtr MsgWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void HeartbeatTick(object? state)
    {
        try
        {
            var hwnd = GetForegroundWindow();
            LogForegroundWindow(hwnd);
        }
        catch (Exception ex)
        {
            FileLogger.LogError($"Error in HeartbeatTick: {ex.Message}");
        }
    }

    private void LogForegroundWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        GetWindowThreadProcessId(hwnd, out uint pid);
        var processName = GetProcessName(pid);
        var title = GetWindowTitle(hwnd);

        lock (_debounceLock)
        {
            if (ShouldIgnore(processName, title))
            {
                _transientIgnored = true;
                return;
            }

            // Debounce duplicates unless a transient window was ignored in between
            if (!_transientIgnored && _lastReportedProcess == processName && _lastReportedTitle == title)
                return;

            _transientIgnored = false;
            _lastReportedProcess = processName;
            _lastReportedTitle = title;

            // Never log window titles in sessionagent.log — log process name only.
            FileLogger.LogInfo($"Foreground: {processName}");

            EnqueueEvent(processName, title);
        }
    }

    private void EnqueueEvent(string processName, string title)
    {
        // BoundedChannel with DropOldest keeps only the latest pending event
        _sendChannel.Writer.TryWrite((processName, title));
    }

    /// <summary>
    /// Single-consumer loop that reads from the channel and POSTs events in order.
    /// On failure, keeps only the latest pending event and retries with backoff (2s to 10s).
    /// </summary>
    private async Task SenderLoopAsync(CancellationToken token)
    {
        var reader = _sendChannel.Reader;
        int backoffMs = 0;

        while (await reader.WaitToReadAsync(token).ConfigureAwait(false))
        {
            while (reader.TryRead(out var item))
            {
                bool success = false;
                while (!success && !token.IsCancellationRequested)
                {
                    if (backoffMs > 0)
                    {
                        try { await Task.Delay(backoffMs, token); }
                        catch (OperationCanceledException) { return; }
                    }

                    // Before sending, drain any newer events (keep only the latest)
                    while (reader.TryRead(out var newer))
                    {
                        item = newer;
                    }

                    success = await TrySendWindowEvent(item.Process, item.Title);
                    if (success)
                    {
                        backoffMs = 0;
                    }
                    else
                    {
                        // Backoff: 2s, 4s, 6s, 8s, 10s cap
                        backoffMs = Math.Min(backoffMs + 2000, 10000);
                        if (backoffMs == 0) backoffMs = 2000;
                    }
                }
            }
        }
    }

    private async Task<bool> TrySendWindowEvent(string processName, string title)
    {
        try
        {
            var payload = new WindowEventPayload
            {
                ProcessOrDomain = processName,
                Title = title
            };
            var json = JsonSerializer.Serialize(payload, WindowEventPayloadContext.Default.WindowEventPayload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync($"http://127.0.0.1:{_config.HttpPort}/window", content);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            FileLogger.LogError($"Failed to post window event: {ex.Message}");
            return false;
        }
    }

    private static string NormalizeProcessName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return string.Empty;
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return name[..^4];
        return name;
    }

    private bool ShouldIgnore(string processName, string title)
    {
        var normalized = NormalizeProcessName(processName).ToLowerInvariant();
        
        // Hardcoded exclusions for lock screen to prevent double-counting locked time
        if (normalized == "lockapp" || normalized == "logonui")
            return true;

        // Transient shell UI flyouts: Quick settings, Start, Search
        if (normalized is "shellhost" or "shellexperiencehost" or "searchhost" or "startmenuexperiencehost")
            return true;

        // Explorer transient windows (Task Switching, Task View, UnlockingWindow, or empty title)
        // Keep "Program Manager" (desktop) and normal File Explorer folders as normal windows.
        if (normalized == "explorer")
        {
            var trimmedTitle = title?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(trimmedTitle) ||
                string.Equals(trimmedTitle, "Task Switching", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmedTitle, "Task View", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmedTitle, "UnlockingWindow", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
            
        if (_config.ExcludeProcesses.Count > 0 && _config.ExcludeProcesses.Exists(p => NormalizeProcessName(p).ToLowerInvariant() == normalized))
            return true;
        if (_config.IncludeProcesses.Count > 0 && !_config.IncludeProcesses.Exists(p => NormalizeProcessName(p).ToLowerInvariant() == normalized))
            return true;
        return false;
    }

    private string GetProcessName(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch
        {
            return "Unknown";
        }
    }

    private string GetWindowTitle(IntPtr hwnd)
    {
        int length = GetWindowTextLength(hwnd);
        if (length == 0) return string.Empty;

        var builder = new StringBuilder(length + 1);
        GetWindowText(hwnd, builder, builder.Capacity);
        return builder.ToString();
    }

    private const uint WM_QUIT = 0x0012;
    private static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage([In] ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage([In] ref MSG lpmsg);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx([In] ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
