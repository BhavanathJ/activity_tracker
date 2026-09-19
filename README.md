# Activity Tracker

A privacy-first, offline Windows activity tracker built in C#/.NET 8. It uses Win32 APIs for tracking window focus and system events, stores data in a DPAPI-protected SQLCipher SQLite database, and provides a CLI for local reporting.

## Features
- **Window Focus Tracking:** Monitors active application and window titles.
- **Browser & Audio Tracking:** A Manifest V3 extension tracks domain focus and background audio for Chrome, Brave, and Edge.
- **Encrypted Local Storage:** Zero cloud calls. Data is protected at rest with a DPAPI-encrypted SQLCipher database key.
- **Retention & Rollups:** Automatically summarizes data older than 90 days.
- **Reporting:** Color-coded `tracker report` CLI via Spectre.Console.

## Building the .NET Service (Native AOT)

The core tracker is designed to run as a Native AOT Windows Service for low resource usage.

1. Open a terminal in the project root.
2. Build the Service with Native AOT:
   ```cmd
   dotnet publish ActivityTracker.Service\ActivityTracker.Service.csproj -c Release -r win-x64 --self-contained true
   ```
   *(Note: This requires C++ build tools installed via Visual Studio to compile AOT).*
3. Build the CLI tool:
   ```cmd
   dotnet publish ActivityTracker.Cli\ActivityTracker.Cli.csproj -c Release -r win-x64
   ```

## Registering as a Windows Service

To have the tracker run continuously in the background and start on boot:

1. Open an **Administrator** command prompt.
2. Register the service using `sc.exe` pointing to the Native AOT `.exe`:
   ```cmd
   sc.exe create "ActivityTrackerService" binPath= "C:\path\to\ActivityTracker.Service.exe" start= auto
   ```
3. Start the service:
   ```cmd
   sc.exe start "ActivityTrackerService"
   ```

### First-Run Key Generation

On the first run (either manually or via the Service), the application will generate a secure 256-bit encryption key using `RandomNumberGenerator`. This key is then encrypted via Windows DPAPI (`DataProtectionScope.LocalMachine`) and saved to `%PROGRAMDATA%\ActivityTracker\db.key`. 

The database itself is stored in `%PROGRAMDATA%\ActivityTracker\tracker.db`.

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
