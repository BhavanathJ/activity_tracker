using System;
using System.Collections.Generic;
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
    private string _lastReportedReason = "";
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
            string reason = "";
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
                    // 1. GSMTC match (unchanged) -> playing, reason "gsmtc".
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
                        reason = "gsmtc";
                    }
                    else
                    {
                        // 2. NEW per-process audio-session check IsForegroundProcessRenderingAudio(fgProcessName)
                        bool comThrewException = false;
                        bool isRenderingAudio = false;
                        string audioSessionReason = "";

                        try
                        {
                            isRenderingAudio = IsForegroundProcessRenderingAudio(fgProcess, out audioSessionReason);
                        }
                        catch (Exception ex)
                        {
                            FileLogger.LogWarn($"Audio session check error: {ex.Message}");
                            comThrewException = true;
                        }

                        if (!comThrewException)
                        {
                            if (isRenderingAudio)
                            {
                                isPlaying = true;
                                reason = audioSessionReason;
                            }
                            else
                            {
                                // If enumeration succeeded and found nothing, playing = false.
                                isPlaying = false;
                                reason = "";
                            }
                        }
                        else
                        {
                            // 3. FALLBACK ONLY if the COM enumeration threw an exception (not when it simply found no matching session):
                            // the old logic (fullscreen AND default-device peak > 0.001), reason "fullscreen-peak".
                            bool isFullscreen = IsForegroundWindowFullscreen(fgHwnd);
                            if (isFullscreen)
                            {
                                bool hasAudio = CheckAudioPeakAboveThreshold(0.001f);
                                if (hasAudio)
                                {
                                    isPlaying = true;
                                    reason = "fullscreen-peak";
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                FileLogger.LogWarn($"Media check error: {ex.Message}");
                isPlaying = false;
                reason = "";
            }

            // 4. Log (FileLogger.LogInfo) only when isPlaying changes or the reason changes: "Media: playing={bool} reason={reason} process={fgProcess}". No per-poll logging; never log window titles.
            bool playingChanged = _lastReportedPlaying != isPlaying;
            bool reasonChanged = !string.Equals(_lastReportedReason, reason, StringComparison.OrdinalIgnoreCase);
            bool processChangedWhilePlaying = isPlaying && !string.Equals(_lastReportedProcess, fgProcess, StringComparison.OrdinalIgnoreCase);

            if (playingChanged || reasonChanged || processChangedWhilePlaying)
            {
                FileLogger.LogInfo($"Media: playing={isPlaying} reason={reason} process={fgProcess}");
            }

            var now = DateTimeOffset.UtcNow;
            bool statusChanged = playingChanged || processChangedWhilePlaying;
            bool heartbeatDue = (now - _lastPostTime) >= TimeSpan.FromSeconds(30);

            if (statusChanged || heartbeatDue)
            {
                if (PostMedia(isPlaying, fgProcess))
                {
                    _lastReportedPlaying = isPlaying;
                    _lastReportedReason = reason;
                    _lastReportedProcess = fgProcess;
                    _lastPostTime = now;
                }
            }
            else if (reasonChanged)
            {
                _lastReportedReason = reason;
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

    private static bool s_hasLoggedMeterQi = false;
    private static readonly Guid IID_IAudioMeterInformation = new("C02216F6-038E-4B54-9D00-D008E73E0064");

    private static bool IsForegroundProcessRenderingAudio(string fgProcessName, out string reason)
    {
        reason = "";
        if (string.IsNullOrEmpty(fgProcessName))
            return false;

        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? deviceCollection = null;

        try
        {
            var enumeratorType = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"));
            if (enumeratorType == null)
                throw new InvalidOperationException("Could not get MMDeviceEnumerator type.");

            enumerator = (IMMDeviceEnumerator?)Activator.CreateInstance(enumeratorType);
            if (enumerator == null)
                throw new InvalidOperationException("Could not create MMDeviceEnumerator.");

            // Enumerate ALL active render endpoints (eRender=0, DEVICE_STATE_ACTIVE=1)
            int hr = enumerator.EnumAudioEndpoints(0, 1, out deviceCollection);
            if (hr != 0 || deviceCollection == null)
            {
                throw new COMException("EnumAudioEndpoints failed", hr);
            }

            hr = deviceCollection.GetCount(out uint deviceCount);
            if (hr != 0)
            {
                throw new COMException("IMMDeviceCollection.GetCount failed", hr);
            }

            for (uint d = 0; d < deviceCount; d++)
            {
                IMMDevice? device = null;
                IAudioSessionManager2? sessionManager = null;
                IAudioSessionEnumerator? sessionEnum = null;
                var sessionsToRelease = new List<IAudioSessionControl>();

                try
                {
                    if (deviceCollection.Item(d, out device) != 0 || device == null)
                        continue;

                    Guid iidSessionManager2 = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
                    if (device.Activate(ref iidSessionManager2, 1 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, out object sessionMgrObj) != 0 ||
                        sessionMgrObj is not IAudioSessionManager2 sm)
                    {
                        continue;
                    }
                    sessionManager = sm;

                    if (sessionManager.GetSessionEnumerator(out sessionEnum) != 0 || sessionEnum == null)
                        continue;

                    if (sessionEnum.GetCount(out int sessionCount) != 0)
                        continue;

                    for (int i = 0; i < sessionCount; i++)
                    {
                        if (sessionEnum.GetSession(i, out IAudioSessionControl sessionControl) != 0 || sessionControl == null)
                            continue;

                        sessionsToRelease.Add(sessionControl);

                        if (sessionControl is not IAudioSessionControl2 sessionControl2)
                            continue;

                        if (sessionControl2.GetProcessId(out uint pid) != 0 || pid == 0)
                            continue;

                        string sessionProcName;
                        try
                        {
                            using var proc = Process.GetProcessById((int)pid);
                            sessionProcName = proc.ProcessName;
                        }
                        catch
                        {
                            continue;
                        }

                        if (!string.Equals(sessionProcName, fgProcessName, StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (sessionControl2.GetState(out int state) != 0 || state != 1 /* AudioSessionStateActive */)
                            continue;

                        // 3a. Try to get the meter: Marshal.GetIUnknownForObject -> Marshal.QueryInterface -> Marshal.GetObjectForIUnknown
                        IntPtr unkPtr = IntPtr.Zero;
                        IntPtr meterPtr = IntPtr.Zero;
                        IAudioMeterInformation? meter = null;
                        try
                        {
                            unkPtr = Marshal.GetIUnknownForObject(sessionControl);
                            Guid iidMeter = IID_IAudioMeterInformation;
                            int qiHr = Marshal.QueryInterface(unkPtr, ref iidMeter, out meterPtr);
                            if (!s_hasLoggedMeterQi)
                            {
                                s_hasLoggedMeterQi = true;
                                FileLogger.LogInfo($"Audio session meter QI hr=0x{qiHr:X8}");
                            }

                            if (qiHr == 0 && meterPtr != IntPtr.Zero)
                            {
                                meter = Marshal.GetObjectForIUnknown(meterPtr) as IAudioMeterInformation;
                            }
                        }
                        finally
                        {
                            if (meterPtr != IntPtr.Zero) Marshal.Release(meterPtr);
                            if (unkPtr != IntPtr.Zero) Marshal.Release(unkPtr);
                        }

                        // 3b. If the meter is obtained: sample GetPeakValue up to 3 times, 150 ms apart; peak > 0.001 on any sample => playing, reason "audio-session". All zero => NOT playing for this session.
                        if (meter != null)
                        {
                            bool peakDetected = false;
                            try
                            {
                                for (int sample = 0; sample < 3; sample++)
                                {
                                    if (meter.GetPeakValue(out float peak) == 0 && peak > 0.001f)
                                    {
                                        peakDetected = true;
                                        break;
                                    }

                                    if (sample < 2)
                                    {
                                        Thread.Sleep(150);
                                    }
                                }
                            }
                            finally
                            {
                                try { Marshal.ReleaseComObject(meter); } catch { }
                            }

                            if (peakDetected)
                            {
                                reason = "audio-session";
                                return true;
                            }
                            // All zero => NOT playing for this session. Continue checking other sessions/devices.
                        }
                        else
                        {
                            // Session meter QI was NOT obtainable.
                            // 1. Get the device-level meter for the SAME device being enumerated:
                            Guid iidDeviceMeter = IID_IAudioMeterInformation;
                            int meterHr = device.Activate(ref iidDeviceMeter, 1 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, out object deviceMeterObj);
                            IAudioMeterInformation? deviceMeter = null;
                            if (meterHr == 0 && deviceMeterObj is IAudioMeterInformation dm)
                            {
                                deviceMeter = dm;
                            }

                            if (deviceMeter == null)
                            {
                                // 3d. device meter cannot be obtained => old behaviour: playing, reason "audio-session-state"
                                reason = "audio-session-state";
                                return true;
                            }

                            bool devicePeakDetected = false;
                            try
                            {
                                // 3a. device peak > 0.001 on any of 3 samples 150 ms apart => playing, reason "audio-session-peak"
                                for (int sample = 0; sample < 3; sample++)
                                {
                                    if (deviceMeter.GetPeakValue(out float peak) == 0 && peak > 0.001f)
                                    {
                                        devicePeakDetected = true;
                                        break;
                                    }

                                    if (sample < 2)
                                    {
                                        Thread.Sleep(150);
                                    }
                                }
                            }
                            finally
                            {
                                try { Marshal.ReleaseComObject(deviceMeter); } catch { }
                            }

                            if (devicePeakDetected)
                            {
                                reason = "audio-session-peak";
                                return true;
                            }

                            // 2. Count OTHER active sessions on this device:
                            int otherActiveSessions = 0;
                            if (sessionEnum.GetCount(out int totalCount) == 0)
                            {
                                for (int j = 0; j < totalCount; j++)
                                {
                                    if (sessionEnum.GetSession(j, out IAudioSessionControl otherControl) != 0 || otherControl == null)
                                        continue;

                                    try
                                    {
                                        if (otherControl is not IAudioSessionControl2 otherControl2)
                                            continue;

                                        if (otherControl2.GetProcessId(out uint otherPid) != 0 || otherPid == 0)
                                            continue;

                                        string otherProcName;
                                        try
                                        {
                                            using var otherProc = Process.GetProcessById((int)otherPid);
                                            otherProcName = otherProc.ProcessName;
                                        }
                                        catch
                                        {
                                            continue;
                                        }

                                        if (string.Equals(otherProcName, fgProcessName, StringComparison.OrdinalIgnoreCase))
                                            continue;

                                        if (otherControl2.GetState(out int otherState) != 0 || otherState != 1 /* AudioSessionStateActive */)
                                            continue;

                                        otherActiveSessions++;
                                    }
                                    finally
                                    {
                                        try { Marshal.ReleaseComObject(otherControl); } catch { }
                                    }
                                }
                            }

                            if (otherActiveSessions > 0)
                            {
                                // 3c. device peak all zero BUT other active sessions exist => ambiguous; keep old behaviour: playing, reason "audio-session-state"
                                reason = "audio-session-state";
                                return true;
                            }

                            // 3b. device peak all zero AND no other active sessions => NOT playing for this session (paused). Continue to other sessions/devices.
                        }
                    }
                }
                finally
                {
                    foreach (var session in sessionsToRelease)
                    {
                        try { Marshal.ReleaseComObject(session); } catch { }
                    }
                    if (sessionEnum != null) { try { Marshal.ReleaseComObject(sessionEnum); } catch { } }
                    if (sessionManager != null) { try { Marshal.ReleaseComObject(sessionManager); } catch { } }
                    if (device != null) { try { Marshal.ReleaseComObject(device); } catch { } }
                }
            }

            return false;
        }
        finally
        {
            if (deviceCollection != null) { try { Marshal.ReleaseComObject(deviceCollection); } catch { } }
            if (enumerator != null) { try { Marshal.ReleaseComObject(enumerator); } catch { } }
        }
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
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig]
        int GetCount(out uint pcDevices);

        [PreserveSig]
        int Item(uint nDevice, out IMMDevice ppDevice);
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IMMDeviceCollection ppDevices);

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
    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig]
        int GetAudioSessionControl(IntPtr audioSessionGuid, uint streamFlags, out IntPtr sessionControl);

        [PreserveSig]
        int GetSimpleAudioVolume(IntPtr audioSessionGuid, uint streamFlags, out IntPtr audioVolume);

        [PreserveSig]
        int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);

        [PreserveSig]
        int RegisterSessionNotification(IntPtr newNotifications);

        [PreserveSig]
        int UnregisterSessionNotification(IntPtr newNotifications);

        [PreserveSig]
        int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr pDuckNotification);

        [PreserveSig]
        int UnregisterDuckNotification(IntPtr pDuckNotification);
    }

    [ComImport]
    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig]
        int GetCount(out int sessionCount);

        [PreserveSig]
        int GetSession(int sessionIndex, out IAudioSessionControl session);
    }

    [ComImport]
    [Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl
    {
        [PreserveSig]
        int GetState(out int state);

        [PreserveSig]
        int GetDisplayName(out IntPtr displayName);

        [PreserveSig]
        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string displayName, ref Guid eventContext);

        [PreserveSig]
        int GetIconPath(out IntPtr iconPath);

        [PreserveSig]
        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string iconPath, ref Guid eventContext);

        [PreserveSig]
        int GetGroupingParam(out Guid groupingParam);

        [PreserveSig]
        int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);

        [PreserveSig]
        int RegisterAudioSessionNotification(IntPtr newNotifications);

        [PreserveSig]
        int UnregisterAudioSessionNotification(IntPtr newNotifications);
    }

    [ComImport]
    [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        // IAudioSessionControl methods
        [PreserveSig]
        int GetState(out int state);

        [PreserveSig]
        int GetDisplayName(out IntPtr displayName);

        [PreserveSig]
        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string displayName, ref Guid eventContext);

        [PreserveSig]
        int GetIconPath(out IntPtr iconPath);

        [PreserveSig]
        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string iconPath, ref Guid eventContext);

        [PreserveSig]
        int GetGroupingParam(out Guid groupingParam);

        [PreserveSig]
        int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);

        [PreserveSig]
        int RegisterAudioSessionNotification(IntPtr newNotifications);

        [PreserveSig]
        int UnregisterAudioSessionNotification(IntPtr newNotifications);

        // IAudioSessionControl2 methods
        [PreserveSig]
        int GetSessionIdentifier(out IntPtr sessionIdentifier);

        [PreserveSig]
        int GetSessionInstanceIdentifier(out IntPtr sessionInstanceIdentifier);

        [PreserveSig]
        int GetProcessId(out uint processId);

        [PreserveSig]
        int IsSystemSoundsSession();

        [PreserveSig]
        int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
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
