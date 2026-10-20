using System;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using Windows.Media.Control;

namespace ActivityTracker.SessionAgent;

public class MediaMonitor
{
    private readonly string _serviceUrl;
    private readonly HttpClient _httpClient;
    private Timer? _timer;

    private bool? _lastReportedPlaying;
    private string _lastReportedProcess = "";
    private DateTimeOffset _lastPostTime = DateTimeOffset.MinValue;
    private readonly object _pollLock = new();

    public MediaMonitor(int httpPort)
    {
        _serviceUrl = $"http://127.0.0.1:{httpPort}/media";
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    }

    public void Start()
    {
        FileLogger.LogInfo("Media monitor started (poll=5s, heartbeat=30s).");
        _timer = new Timer(CheckMediaCallback, null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    public void Stop()
    {
        _timer?.Dispose();
        _httpClient.Dispose();
        FileLogger.LogInfo("Media monitor stopped.");
    }

    private void CheckMediaCallback(object? state)
    {
        if (!Monitor.TryEnter(_pollLock))
            return;

        try
        {
            bool isPlaying = false;
            string fgProcess = "";

            try
            {
                IntPtr fgHwnd = GetForegroundWindow();
                fgProcess = GetProcessName(fgHwnd);
                string fgLower = fgProcess.ToLowerInvariant();

                // Desktop, lock screen, and empty processes are never active media apps
                if (!string.IsNullOrEmpty(fgLower) &&
                    fgLower != "explorer" &&
                    fgLower != "lockapp" &&
                    fgLower != "logonui")
                {
                    bool sessionMatches = false;
                    try
                    {
                        var op = GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                        var mgr = op.GetAwaiter().GetResult();
                        var sessions = mgr?.GetSessions();
                        if (sessions != null)
                        {
                            foreach (var session in sessions)
                            {
                                try
                                {
                                    var pb = session.GetPlaybackInfo();
                                    if (pb != null && pb.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                                    {
                                        string appId = (session.SourceAppUserModelId ?? "").ToLowerInvariant();
                                        if (DoesAppIdMatchForeground(appId, fgLower))
                                        {
                                            sessionMatches = true;
                                            break;
                                        }
                                    }
                                }
                                catch
                                {
                                    // Ignore individual session query errors
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        FileLogger.LogWarn($"GSMTC query error: {ex.Message}");
                    }

                    if (sessionMatches)
                    {
                        isPlaying = true;
                    }
                    else
                    {
                        bool isFullscreen = IsForegroundWindowFullscreen(fgHwnd);
                        if (isFullscreen)
                        {
                            bool hasAudio = CheckAudioPeakAboveThreshold(0.001f);
                            if (hasAudio)
                            {
                                isPlaying = true;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                FileLogger.LogWarn($"Media check error: {ex.Message}");
                isPlaying = false;
            }

            var now = DateTimeOffset.UtcNow;
            bool changed = (_lastReportedPlaying != isPlaying) ||
                           (isPlaying && !string.Equals(_lastReportedProcess, fgProcess, StringComparison.OrdinalIgnoreCase));
            bool heartbeatDue = (now - _lastPostTime) >= TimeSpan.FromSeconds(30);

            if (changed || heartbeatDue)
            {
                if (PostMedia(isPlaying, fgProcess))
                {
                    _lastReportedPlaying = isPlaying;
                    _lastReportedProcess = fgProcess;
                    _lastPostTime = now;
                }
            }
        }
        finally
        {
            Monitor.Exit(_pollLock);
        }
    }

    private bool PostMedia(bool playing, string process)
    {
        try
        {
            var payload = new MediaEventPayload
            {
                Source = "agent",
                Playing = playing,
                Process = process
            };
            var json = JsonSerializer.Serialize(payload, MediaEventPayloadContext.Default.MediaEventPayload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = _httpClient.PostAsync(_serviceUrl, content).GetAwaiter().GetResult();
            FileLogger.LogInfo($"POST /media (Playing={playing}, Process={process}) -> {(int)response.StatusCode}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            FileLogger.LogWarn($"POST /media failed: {ex.Message}");
            return false;
        }
    }

    private static string GetProcessName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";
        try
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return "";
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch
        {
            return "";
        }
    }

    private static bool IsForegroundWindowFullscreen(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        if (hwnd == GetDesktopWindow() || hwnd == GetShellWindow()) return false;

        if (!GetWindowRect(hwnd, out RECT winRect)) return false;
        if (winRect.Left == 0 && winRect.Top == 0 && winRect.Right == 0 && winRect.Bottom == 0) return false;

        IntPtr hMonitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (hMonitor == IntPtr.Zero) return false;

        MONITORINFO mi = new MONITORINFO();
        mi.cbSize = Marshal.SizeOf<MONITORINFO>();
        if (!GetMonitorInfo(hMonitor, ref mi)) return false;

        // Check if window rect equals the monitor rect (exact or covering borderless)
        bool exact = (winRect.Left == mi.rcMonitor.Left &&
                      winRect.Top == mi.rcMonitor.Top &&
                      winRect.Right == mi.rcMonitor.Right &&
                      winRect.Bottom == mi.rcMonitor.Bottom);

        bool covers = (winRect.Left <= mi.rcMonitor.Left &&
                       winRect.Top <= mi.rcMonitor.Top &&
                       winRect.Right >= mi.rcMonitor.Right &&
                       winRect.Bottom >= mi.rcMonitor.Bottom);

        return exact || covers;
    }

    private static readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.HashSet<string>> AppAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["brave"] = new(StringComparer.OrdinalIgnoreCase) { "brave", "brave.exe", "bravesoftware.brave" },
        ["chrome"] = new(StringComparer.OrdinalIgnoreCase) { "chrome", "chrome.exe", "google.chrome" },
        ["msedge"] = new(StringComparer.OrdinalIgnoreCase) { "msedge", "msedge.exe", "microsoft.microsoftedge", "microsoftedge" },
        ["spotify"] = new(StringComparer.OrdinalIgnoreCase) { "spotify", "spotify.exe", "spotifyab.spotifymusic" },
        ["vlc"] = new(StringComparer.OrdinalIgnoreCase) { "vlc", "vlc.exe", "videolan.vlc" }
    };

    private static bool DoesAppIdMatchForeground(string appId, string fgLower)
    {
        if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(fgLower))
            return false;

        // Exact match
        if (string.Equals(appId, fgLower, StringComparison.OrdinalIgnoreCase))
            return true;

        string appIdNoExe = appId.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? appId[..^4]
            : appId;

        if (string.Equals(appIdNoExe, fgLower, StringComparison.OrdinalIgnoreCase))
            return true;

        // Alias map check for brave, chrome, msedge, spotify, vlc
        if (AppAliases.TryGetValue(fgLower, out var aliases))
        {
            if (aliases.Contains(appId) || aliases.Contains(appIdNoExe))
                return true;

            foreach (var alias in aliases)
            {
                if (appId.StartsWith(alias + "_", StringComparison.OrdinalIgnoreCase) ||
                    appId.StartsWith(alias + ".", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool CheckAudioPeakAboveThreshold(float threshold)
    {
        try
        {
            var enumeratorType = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"));
            if (enumeratorType == null) return false;
            var enumerator = (IMMDeviceEnumerator?)Activator.CreateInstance(enumeratorType);
            if (enumerator == null) return false;

            // 0 = eRender, 0 = eConsole (fallback to 1 = eMultimedia)
            int hr = enumerator.GetDefaultAudioEndpoint(0, 0, out IMMDevice device);
            if (hr != 0 || device == null)
            {
                hr = enumerator.GetDefaultAudioEndpoint(0, 1, out device);
                if (hr != 0 || device == null)
                {
                    Marshal.ReleaseComObject(enumerator);
                    return false;
                }
            }

            Guid iid = typeof(IAudioMeterInformation).GUID;
            hr = device.Activate(ref iid, 1 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, out object meterObj);
            if (hr != 0 || meterObj is not IAudioMeterInformation meter)
            {
                Marshal.ReleaseComObject(device);
                Marshal.ReleaseComObject(enumerator);
                return false;
            }

            try
            {
                // Sample 3 times over ~300 ms (0ms, 150ms, 300ms)
                for (int i = 0; i < 3; i++)
                {
                    if (meter.GetPeakValue(out float peak) == 0)
                    {
                        if (peak > threshold)
                            return true;
                    }

                    if (i < 2)
                    {
                        Thread.Sleep(150);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(meter);
                Marshal.ReleaseComObject(device);
                Marshal.ReleaseComObject(enumerator);
            }

            return false;
        }
        catch (Exception ex)
        {
            FileLogger.LogWarn($"Audio peak check error: {ex.Message}");
            return false;
        }
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IntPtr ppDevices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(IntPtr pClient);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IntPtr pClient);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);

        [PreserveSig]
        int OpenPropertyStore(int stgmAccess, out IntPtr ppProperties);

        [PreserveSig]
        int GetId(out IntPtr ppstrId);

        [PreserveSig]
        int GetState(out int pdwState);
    }

    [ComImport]
    [Guid("C02216F6-038E-4B54-9D00-D008E73E0064")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig]
        int GetPeakValue(out float pfPeak);

        [PreserveSig]
        int GetMeteringChannelCount(out uint pnChannelCount);

        [PreserveSig]
        int GetChannelsPeakValues(uint u32ChannelCount, IntPtr afPeakValues);

        [PreserveSig]
        int QueryHardwareSupport(out uint pdwHardwareSupportMask);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();
}
