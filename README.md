# Activity Tracker

A privacy-first, offline Windows activity tracker built in C#/.NET 8. It uses Win32 APIs for tracking window focus and system events, stores data in a DPAPI-protected SQLCipher SQLite database, and provides a CLI for local reporting.

## Architecture

Because Windows isolates services in Session 0 (non-interactive), Activity Tracker is split into a multi-process architecture to securely and effectively track user activity:

1. **ActivityTracker.Service:** A Native AOT background Windows Service. It owns the SQLite database, handles data retention, and hosts a local HTTP listener on port 14321.
2. **ActivityTracker.SessionAgent:** A lightweight user-session process that runs via Task Scheduler "At log on." It monitors window focus changes and system idle state using Win32 hooks, sending events to the Service via HTTP.
3. **Browser Extension:** A Manifest V3 extension that tracks active tabs and audible background media (e.g., music, videos) for Chromium-based browsers, reporting back to the Service via HTTP.
4. **ActivityTracker.Cli:** A Spectre.Console-powered CLI tool for querying the encrypted database and generating activity reports.

## Features
- **Window Focus Tracking:** Monitors active application and window titles.
- **Browser & Audio Tracking:** Tracks domain focus and background audio for Chrome, Brave, and Edge.
- **Idle Detection:** Automatically pauses tracking when no mouse/keyboard input is detected.
- **Encrypted Local Storage:** Zero cloud calls. Data is protected at rest with a DPAPI-encrypted SQLCipher database key.
- **Configurable Rules:** Include or exclude specific applications or domains via a JSON configuration file.
- **Retention & Rollups:** Automatically summarizes data older than 90 days.
- **Reporting:** Color-coded `tracker report` CLI.

## Configuration

The service and agent read configuration from `%PROGRAMDATA%\ActivityTracker\config.json`. If the file doesn't exist, it will be generated on first run.

```json
{
  "IncludeProcesses": [],
  "ExcludeProcesses": ["Taskmgr", "SearchApp"],
  "IncludeDomains": [],
  "ExcludeDomains": ["localhost", "127.0.0.1"],
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
   *(Note: This requires C++ build tools installed via Visual Studio to compile AOT).*
3. Build the SessionAgent:
   ```cmd
   dotnet publish ActivityTracker.SessionAgent\ActivityTracker.SessionAgent.csproj -c Release -r win-x64
   ```
4. Build the CLI tool:
   ```cmd
   dotnet publish ActivityTracker.Cli\ActivityTracker.Cli.csproj -c Release -r win-x64
   ```

## Installation & Registration

### 1. Register the Windows Service
To have the backend run continuously and start on boot:

1. Open an **Administrator** command prompt.
2. Register the service using `sc.exe` pointing to the Native AOT `.exe`:
   ```cmd
   sc.exe create "ActivityTrackerService" binPath= "C:\path\to\ActivityTracker.Service.exe" start= auto
   ```
3. Start the service:
   ```cmd
   sc.exe start "ActivityTrackerService"
   ```

### 2. Setup the SessionAgent
The SessionAgent must run under your logged-on user session to track windows and idle time correctly.

1. Open **Task Scheduler**.
2. Click **Create Task**.
3. **General tab:** Name it "ActivityTracker SessionAgent".
4. **Triggers tab:** New -> Begin the task: **At log on** -> Any user.
5. **Actions tab:** New -> Start a program -> Browse to your compiled `ActivityTracker.SessionAgent.exe`.
6. **Settings tab:** Uncheck "Stop the task if it runs longer than 3 days".
7. Run the task manually or log out and back in.

*(On first run, a secure 256-bit encryption key is generated, encrypted via Windows DPAPI, and saved to `%PROGRAMDATA%\ActivityTracker\db.key`. The database is created at `%PROGRAMDATA%\ActivityTracker\tracker.db`.)*

## Browser Extension Installation

To track web domains and audio, install the included Manifest V3 extension.

### Configuration
Before loading the extension into a browser, open `Extension\config.js` and edit the `BROWSER_ID` to match the target browser:
```javascript
export const BROWSER_ID = "chrome"; // Change to "brave" or "edge" as needed
```

### Installation
1. Go to the extensions management page:
   - Chrome: `chrome://extensions/`
   - Edge: `edge://extensions/`
   - Brave: `brave://extensions/`
2. Enable **Developer mode** (toggle in the top right).
3. Click **Load unpacked** and select the `Extension` directory.
4. *Repeat this process for each browser you use, ensuring you change the `BROWSER_ID` in `config.js` before loading it into each.*

## Usage

Use the CLI to generate reports. The CLI queries the encrypted database securely.

```cmd
ActivityTracker.Cli.exe report --today
ActivityTracker.Cli.exe report --week
ActivityTracker.Cli.exe report --month 2026-09
```
