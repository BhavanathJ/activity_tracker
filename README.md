# Activity Tracker

A privacy-first, offline Windows activity tracker built in C#/.NET 8. It uses Win32 APIs for tracking window focus and system events, stores data in a DPAPI-protected SQLCipher SQLite database, and provides a CLI for local reporting.

## Architecture

Because Windows isolates services in Session 0 (non-interactive), Activity Tracker is split into a multi-process architecture to securely and effectively track user activity:

1. **ActivityTracker.Service:** A Native AOT background Windows Service. It owns the SQLite database, handles data retention, and hosts a local HTTP listener on port 14321.
2. **ActivityTracker.SessionAgent:** A lightweight user-session process that runs via Task Scheduler "At log on." It monitors window focus changes, media playback state (via WinRT GSMTC), and system idle state using Win32 hooks, sending events to the Service via HTTP.
3. **Browser Extension:** A Manifest V3 extension that tracks active tabs and audible background media for Chromium-based browsers (Chrome, Brave, Edge), reporting back to the Service via HTTP.
4. **ActivityTracker.Cli:** A Spectre.Console-powered CLI tool for querying the encrypted database and generating activity reports.

## Features
- **Window Focus Tracking:** Monitors active application and window titles.
- **Browser & Audio Tracking:** Tracks domain focus and background audio for Chrome, Brave, and Edge.
- **Media-Aware Idle Detection:** User is only considered idle when there is no keyboard/mouse input AND nothing is playing media in the foreground app.
- **Encrypted Local Storage:** Zero cloud calls. Data is protected at rest with a DPAPI-encrypted SQLCipher database key.
- **Configurable Rules:** Include or exclude specific applications or domains via a JSON configuration file.
- **Retention & Rollups:** Automatically summarizes data older than 90 days into monthly rollups.
- **Reporting:** Color-coded `tracker report` CLI with timeline, daily, weekly, and monthly reports, plus data quality diagnostics.

## Threat Model & Encryption

The SQLite database is encrypted with SQLCipher using a key protected by Windows DPAPI (`DataProtectionScope.LocalMachine`).

> [!NOTE]
> Because `LocalMachine` scope is used to allow the Session 0 background service to access the database across system restarts and user sessions, any process running locally on the machine with appropriate local permissions can invoke DPAPI to decrypt the key. The encryption is designed to protect your sensitive activity history against **offline disk theft** (e.g., extracting the drive or copying files from an unbooted machine). It does not protect against malware or untrusted applications already executing locally on your system.

## Configuration

The service and agent read configuration from `%PROGRAMDATA%\ActivityTracker\config.json`. If the file doesn't exist, it will be generated on first run with the real defaults:

```json
{
  "IncludeProcesses": [],
  "ExcludeProcesses": [],
  "IncludeDomains": [],
  "ExcludeDomains": [],
  "IdleTimeoutSeconds": 180,
  "HttpPort": 14321
}
```

## Building the Solution

1. Open a terminal in the project root.
2. Build the Service (Native AOT):
   ```cmd
   dotnet publish ActivityTracker.Service\ActivityTracker.Service.csproj -c Release -r win-x64 --self-contained true
   ```
   *(Note: This compiles to native code via Native AOT; requires C++ build tools installed via Visual Studio).*
3. Build the SessionAgent:
   ```cmd
   dotnet publish ActivityTracker.SessionAgent\ActivityTracker.SessionAgent.csproj -c Release -r win-x64 --self-contained true
   ```
4. Build the CLI tool:
   ```cmd
   dotnet publish ActivityTracker.Cli\ActivityTracker.Cli.csproj -c Release -r win-x64 --self-contained true
   ```

## Installation & Registration

An automated administrative installation script is provided in the repository root:

```powershell
# Open an elevated (Administrator) PowerShell prompt:
.\InstallAndTestService.ps1
```

This script:
1. Validates that Administrator privileges are present.
2. Sets strict ACLs on `%PROGRAMDATA%\ActivityTracker` (`SYSTEM:F`, `Administrators:F`, `User:M`).
3. Idempotently recreates the `ActivityTrackerService` with automatic failure restart policies (restarting after 5s/5s/60s).
4. Registers and launches the `ActivityTrackerSessionAgent` scheduled task for the current interactive user.
5. Verifies service status and connectivity to `http://127.0.0.1:14321/config`.

---

## Browser Extension Installation

To track web domains and active audio, install the included Manifest V3 extension.

### Configuration
Before loading the extension into a browser, open `Extension\config.js` and edit the `BROWSER_ID` to match the target browser:
```javascript
export const BROWSER_ID = "brave"; // Change to "chrome" or "edge" as needed
```

### Installation
1. Go to the extensions management page:
   - Chrome: `chrome://extensions/`
   - Edge: `edge://extensions/`
   - Brave: `brave://extensions/`
2. Enable **Developer mode** (toggle in the top right).
3. Click **Load unpacked** and select the `Extension` directory.
4. **Per-Browser Setup & Reloading:**
   Repeat this process for each browser you use, ensuring you change the `BROWSER_ID` in `config.js` before loading it into each.
   If you ever modify `config.js` or extension scripts, navigate to the extensions page and click the circular **Reload** button on the extension card to reload the service worker.

---

## Usage

Use the CLI to generate reports. The CLI queries the encrypted database securely.

### Timeframe Reports
```cmd
ActivityTracker.Cli.exe report --today
ActivityTracker.Cli.exe report --week
ActivityTracker.Cli.exe report --month 2026-09
```
*After `--today` and `--week` reports, a **Data quality** footer is printed showing open rows, single rows exceeding 2 hours, and domain focus validation warnings.*

### Timeline Report
To inspect all events (window, browser, audio, lock) in chronological order for a time range:
```cmd
ActivityTracker.Cli.exe report --timeline --from 09:00 --to 17:00
ActivityTracker.Cli.exe report --timeline --from 14:00 --to 16:30 --date 2026-10-20
```

### Diagnostic Raw Dump
To dump raw database event rows for a specific process or domain on today or a past day:
```cmd
ActivityTracker.Cli.exe report --raw vlc
ActivityTracker.Cli.exe report --raw chrome --date 2026-10-18
```

---

## Troubleshooting

### Service State
Check whether the service is running:
```cmd
sc.exe query ActivityTrackerService
```
Start or restart the service:
```cmd
sc.exe stop ActivityTrackerService
sc.exe start ActivityTrackerService
```

### Verifying HTTP Listener
Test whether the local listener is responding:
```cmd
curl http://127.0.0.1:14321/config
```

### Log Paths
If events are not being recorded or a component encounters an error, inspect the local log files:
- **Service Log:** `%PROGRAMDATA%\ActivityTracker\logs\service.log`
- **SessionAgent Log:** `%PROGRAMDATA%\ActivityTracker\logs\sessionagent.log`

### Browser Extension Troubleshooting
- If tab or domain tracking stops working after updating `Extension\config.js` or `Extension\background.js`, go to `chrome://extensions` (or `brave://extensions` / `edge://extensions`) and click the circular **Reload** button on the Activity Tracker Extension card to restart the service worker.
- Verify background service worker console output via **Inspect views: service worker** on the extensions page.
