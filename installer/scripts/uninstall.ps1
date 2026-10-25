# uninstall.ps1
# Removes Activity Tracker service, session agent task, and optionally deletes data.
# Must be run as Administrator.
#
# Parameters:
#   -DeleteData  If present, deletes C:\ProgramData\ActivityTracker and all recorded data.

param(
    [switch]$DeleteData
)

$ErrorActionPreference = "Stop"

function Write-Step($message) {
    Write-Host $message
}

function Warn-Step($message) {
    Write-Host "WARNING: $message" -ForegroundColor Yellow
}

# ── Check for Administrator ──
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]$identity
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host ""
    Write-Error "This script must be run as Administrator."
    exit 1
}

# ── Stop and delete service ──
$serviceName = "ActivityTrackerService"
Write-Step "Stopping service $serviceName..."
$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing) {
    & sc.exe stop $serviceName 2>&1 | Out-Null
    $stopped = $false
    for ($i = 0; $i -lt 30; $i++) {
        $q = & sc.exe query $serviceName
        if (($q -join "`n") -match "STATE\s*:\s*\d+\s+STOPPED") {
            $stopped = $true
            break
        }
        Start-Sleep -Seconds 1
    }
    if (-not $stopped) {
        Warn-Step "$serviceName did not reach STOPPED state within 30 seconds. Attempting forced delete."
    }
    Write-Step "Deleting service $serviceName..."
    & sc.exe delete $serviceName 2>&1 | Out-Null
    Start-Sleep -Seconds 2
} else {
    Write-Step "Service $serviceName not found (already removed)."
}

# ── Stop agent processes ──
Write-Step "Stopping ActivityTracker.SessionAgent processes..."
$taskName = "ActivityTrackerSessionAgent"
$existingTask = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if ($existingTask) {
    Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
}
Get-Process ActivityTracker.SessionAgent -EA SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
for ($i = 0; $i -lt 10; $i++) {
    if (-not (Get-Process ActivityTracker.SessionAgent -EA SilentlyContinue)) { break }
    Start-Sleep -Seconds 1
}

# ── Unregister scheduled task ──
if ($existingTask) {
    Write-Step "Removing scheduled task $taskName..."
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
} else {
    Write-Step "Scheduled task $taskName not found (already removed)."
}

# ── Optionally delete data ──
$programData = "$env:ProgramData\ActivityTracker"
if ($DeleteData) {
    Write-Step "Deleting activity data at $programData..."
    if (Test-Path $programData) {
        Remove-Item -Path $programData -Recurse -Force -ErrorAction SilentlyContinue
        if (Test-Path $programData) {
            Warn-Step "Could not fully remove $programData. Some files may be locked. Remove manually after reboot."
        } else {
            Write-Step "Data deleted."
        }
    } else {
        Write-Step "Data folder not found (already removed)."
    }
} else {
    Write-Step "Activity data at $programData has been preserved."
    Write-Step "To delete it, re-run with: uninstall.ps1 -DeleteData"
}

Write-Host ""
Write-Host "Activity Tracker has been uninstalled." -ForegroundColor Green
exit 0
