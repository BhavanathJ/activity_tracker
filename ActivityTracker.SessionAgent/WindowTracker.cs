using System;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
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

    public WindowTracker(TrackerConfig config)
    {
        _config = config;
    }

    public void Start()
    {
        FileLogger.LogInfo("Starting Window Tracker in SessionAgent...");
        _dele = new WinEventDelegate(WinEventProc);

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
    }

    public void Stop()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
        if (_msgHwnd != IntPtr.Zero)
        {
            PostMessage(_msgHwnd, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }
        FileLogger.LogInfo("Window Tracker stopped.");
    }

    private void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        LogForegroundWindow(hwnd);
    }

    private IntPtr MsgWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void LogForegroundWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        GetWindowThreadProcessId(hwnd, out uint pid);
        var processName = GetProcessName(pid);
        var title = GetWindowTitle(hwnd);

        if (ShouldIgnore(processName)) return;

        // Debounce duplicates
        if (_lastReportedProcess == processName && _lastReportedTitle == title)
            return;

        _lastReportedProcess = processName;
        _lastReportedTitle = title;

        var payload = new WindowEventPayload
        {
            ProcessOrDomain = processName,
            Title = title
        };

        var json = JsonSerializer.Serialize(payload, WindowEventPayloadContext.Default.WindowEventPayload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        _ = Task.Run(async () =>
        {
            try
            {
                await _httpClient.PostAsync($"http://127.0.0.1:{_config.HttpPort}/window", content);
            }
            catch (Exception ex)
            {
                FileLogger.LogError($"Failed to post window event: {ex.Message}");
            }
        });
    }

    private static string NormalizeProcessName(string name)
    {
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return name[..^4];
        return name;
    }

    private bool ShouldIgnore(string processName)
    {
        var normalized = NormalizeProcessName(processName).ToLowerInvariant();
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
