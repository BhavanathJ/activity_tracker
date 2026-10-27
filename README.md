# Activity Tracker

A private, local-only activity tracker for Windows. It records which app and browser site you are using, whether media is playing, and when you are away, then lets you see it all from a command-line dashboard and reports.

- **Local only.** No cloud, no telemetry, no network calls except to `127.0.0.1` on your own PC.
- **Encrypted at rest.** The database is SQLCipher-encrypted; its key is protected by Windows DPAPI.
- **Starts with Windows.** A background service plus a small per-user agent.
- **Visible, not hidden.** It shows up in Installed apps, Services and Task Scheduler, and the installer asks for consent.

> **Status: early release (v0.x).** Tested on **Windows 11 Home, x64** only. Windows 10 (2004+) and other setups are expected to work but are untested. Bug reports are welcome (see [Reporting problems](#reporting-problems)).

---

## Contents
- [Before you install](#before-you-install)
- [Quick install](#quick-install)
- [What it records](#what-it-records)
- [Using it](#using-it)
- [Requirements and limits](#requirements-and-limits)
- [Uninstall](#uninstall)
- [How it works](#how-it-works)
- [Configuration](#configuration)
- [Troubleshooting](#troubleshooting)
- [Build from source](#build-from-source)
- [Responsible use](#responsible-use)

---

## Before you install

You need:
- **Windows 10 (version 2004 or newer) or Windows 11**, 64-bit Intel/AMD (`x64`). Not supported: Windows 7/8, 32-bit Windows, ARM PCs.
- **Administrator rights** on the PC. The installer registers a background service and asks for approval once.
- **Chrome, Brave or Edge** if you want website tracking. Window tracking works without them. Firefox is not supported.
- A few hundred MB of free disk space.
- **Port 14321** free on localhost (the tracker uses it internally).
- The **Microsoft Visual C++ Redistributable (x64)**. Most PCs already have it. If the service fails to start after install, install it from Microsoft and run the installer again. *(This is a precaution: it has not been tested on a PC without it.)*

You do **not** need to install .NET or any developer tools.

---

## Quick install

1. **Download** `ActivityTracker-Setup-<version>.exe` from the latest [Release](https://github.com/BhavanathJ/activity_tracker/releases).
2. **Windows SmartScreen** will warn that the app is from an unknown publisher, because the installer is not code-signed. Click **More info**, then **Run anyway**.
3. *(Optional)* Check the download against `checksums.txt` from the same release:
   ```powershell
   Get-FileHash .\ActivityTracker-Setup-*.exe -Algorithm SHA256
   ```
4. **Run the installer as your normal user account** and approve the administrator prompt. Do not sign in as a different admin account: the tracker records the user who runs the installer. Read and accept the privacy page.
5. **Load the browser extension** (needed for website tracking; window tracking works without it):
   1. Open `chrome://extensions`, `brave://extensions` or `edge://extensions`.
   2. Turn on **Developer mode**.
   3. Click **Load unpacked** and choose the folder for your browser inside the install folder:
      - `C:\Program Files\ActivityTracker\extension-chrome`
      - `C:\Program Files\ActivityTracker\extension-brave`
      - `C:\Program Files\ActivityTracker\extension-edge`
   
   Use only the folder that matches your browser. Repeat for each browser you want tracked.
6. **Check it works.** Open a **new** terminal (an already-open one will not see the new command) and run:
   ```
   tracker live
   ```

Chrome and Edge show a "developer mode extensions" notice when they start, because the extension is loaded unpacked. This is expected.

---

## What it records

| Recorded | Not recorded |
|---|---|
| Foreground app name and **window title** | Keystrokes, mouse movement, screenshots |
| Browser **domain** (e.g. `github.com`) of the active tab | Full URLs, query strings, page content, form data |
| Whether audio or video is playing (and from which app or domain) | The audio or video itself |
| Lock, unlock, logon, logoff, idle/away periods | Anything about other users on the PC |

Be aware that **window titles can contain sensitive text** (document names, video titles, chat names). The data never leaves your PC, but anyone who can use your logged-in session can run the CLI and read it.

Data older than 90 days is summarised into monthly totals.

---

## Using it

After installing, the `tracker` command is available in any new terminal.

### Live view
```
tracker live
tracker live --interval 5
```
Shows the current state (ACTIVE / IDLE / LOCKED), the focused window and browser tab, today's top apps, domains and audio, and recent events. Press `Ctrl+C` to exit. If the output is redirected to a file, it prints one snapshot and exits. A terminal about 120 columns wide gives the best layout.

### Reports
```
tracker report --today
tracker report --week
tracker report --month 2026-10
```
`--today` and `--week` end with a **Data quality** summary (open rows, unusually long rows, validation warnings).

### Timeline
Every event, in order, for a time range:
```
tracker report --timeline --from 09:00 --to 17:00
tracker report --timeline --from 14:00 --to 16:30 --date 2026-10-20
```

### Raw rows (for debugging)
```
tracker report --raw vlc
tracker report --raw chrome --date 2026-10-18
```

### Coverage audit
```
tracker report --coverage
```
Checks that window time has no gaps or double counting.

---

## Requirements and limits

**Requirements**
- Windows 10 version 2004 (build 19041) or newer, or Windows 11
- 64-bit Intel/AMD (`x64`). There is no ARM or 32-bit build.
- No .NET install needed; the installer is self-contained.

**Supported browsers:** Chrome, Brave, Edge (Chromium-based). Firefox and Safari are not supported; window tracking still works for them, but website tracking does not.

**Known limits**
- The installer and binaries are **unsigned**, so SmartScreen warns, and some antivirus products may flag the installer.
- The browser extension must be loaded manually (unpacked).
- It tracks **one Windows user**: the user who ran the installer.
- Microsoft-account and domain-joined PCs are untested.
- Port `14321` on localhost must be free.
- If media is playing, you are treated as active for as long as it plays, with no time cap. A long fully silent stretch in a video (over ~20 seconds) can briefly count as paused.

---

## Uninstall

Settings > **Apps** > **Installed apps** > **Activity Tracker** > **Uninstall**.

The uninstaller removes the service, the agent task and the program files, then asks:

> *Also delete your recorded activity data?*

- **No** keeps `C:\ProgramData\ActivityTracker` (database and key), so a reinstall continues where you left off.
- **Yes** permanently deletes the database and its key. It **cannot be recovered**.

Your browser extension is separate: remove it from the browser's extensions page.

---

## How it works

Windows services run in a non-interactive session and cannot see your desktop, so the tracker is split in four parts:

1. **ActivityTracker.Service**: a Native AOT Windows Service. It owns the database, applies retention, and listens on `127.0.0.1:14321`.
2. **ActivityTracker.SessionAgent**: a small per-user process started at logon by Task Scheduler. It watches window focus, idle time, and media playback, and sends events to the service over local HTTP.
3. **Browser extension** (Manifest V3): reports the active tab's domain and whether the tab is audible.
4. **ActivityTracker.Cli** (`tracker`): reads the database and shows the live view and reports.

**Idle detection.** You count as idle after 3 minutes without keyboard or mouse input, unless media is playing. Media counts as playing when any of these is true:
1. the foreground app has a Windows media session in the *Playing* state;
2. the focused browser's active tab is audible (reported by the extension);
3. the foreground app has an active audio stream on any output device (this covers players such as VLC that do not report a media session).

When playback stops, idle starts after a 20-second grace period. When you do go idle, the open rows are closed at the time of your last input, so away time is not counted.

**Storage.** `C:\ProgramData\ActivityTracker\` holds `tracker.db` (SQLCipher), `db.key` (DPAPI-protected), `config.json` and `logs\`.

### Threat model

The key is protected with DPAPI at `LocalMachine` scope so the background service can open the database across reboots and user sessions. This protects your history against **offline theft** (someone copying the drive or the files from a powered-off machine). It does **not** protect against malware or other software already running on your PC with local permissions, because such a process can ask DPAPI to decrypt the key.

---

## Configuration

`C:\ProgramData\ActivityTracker\config.json` is created on first run:

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

Use `ExcludeProcesses` / `ExcludeDomains` to stop recording specific apps or sites. Restart the service after editing:

```powershell
# Administrator PowerShell
sc.exe stop ActivityTrackerService
sc.exe start ActivityTrackerService
```

---

## Troubleshooting

**`tracker` is not recognised.** Close the terminal and open a new one; PATH is read when a terminal starts. If it still fails, run it by full path: `& "C:\Program Files\ActivityTracker\cli\tracker.cmd" live`.

**Check the service and agent.**
```powershell
Get-Service ActivityTrackerService
Get-ScheduledTask -TaskName ActivityTrackerSessionAgent
Get-Process ActivityTracker.SessionAgent
curl http://127.0.0.1:14321/config
```

**Logs** (in `C:\ProgramData\ActivityTracker\logs\`):
- `install.log`: what the installer did, step by step
- `service.log`: the service
- `sessionagent.log`: the agent

**Websites are not being recorded.** Check that the extension is loaded in that browser and that you chose the folder for that browser (`extension-brave`, `extension-chrome` or `extension-edge`). After changing extension files, click the circular **Reload** button on its card in the extensions page.

**A video player is counted as idle while playing.** Run `tracker report --raw <app>` and look at the `Media:` lines in `sessionagent.log`, then report it with both.

### Reporting problems

Open an issue and attach `install.log` (and the relevant lines from `service.log` / `sessionagent.log`) after removing anything you consider private, such as window titles. Mention your Windows version and edition.

---

## Build from source

Requirements: Windows, the .NET 8 SDK or newer, and (for the Native AOT service) the Visual Studio C++ build tools.

```powershell
dotnet publish ActivityTracker.Service/ActivityTracker.Service.csproj    -c Release -r win-x64 --self-contained true -o out/service
dotnet publish ActivityTracker.SessionAgent/ActivityTracker.SessionAgent.csproj -c Release -r win-x64 --self-contained true -o out/agent
dotnet publish ActivityTracker.Cli/ActivityTracker.Cli.csproj            -c Release -r win-x64 --self-contained true -o out/cli
```

**Developer install** (administrator PowerShell, from the repo root): `.\InstallAndTestService.ps1` registers the service and the agent task for the current user. Load the extension from `Extension\` after setting `BROWSER_ID` in `Extension\config.js` to `brave`, `chrome` or `edge`.

**Build the installer locally** (needs [Inno Setup 6](https://jrsoftware.org/isinfo.php)):
```powershell
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" /DVersion=0.0.0-dev /DServiceDir=out\service /DAgentDir=out\agent /DCliDir=out\cli installer\ActivityTracker.iss
```
The installer is written to `installer\output\`. Pushing a tag such as `v0.1.0` makes GitHub Actions build and publish a release (installer, per-browser extension zips and `checksums.txt`).

---

## Responsible use

Activity Tracker is for **your own PC**, to understand your own habits.

- Install it only on a computer you own, as the person whose activity it records.
- **Do not install it on anyone else's device, or without their knowledge and informed consent.** It is not covert monitoring software, and it has no hidden or stealth mode.
- Respect the laws and workplace policies that apply to you. Employer-managed devices may forbid installing it.

## License

MIT. See [LICENSE](LICENSE).