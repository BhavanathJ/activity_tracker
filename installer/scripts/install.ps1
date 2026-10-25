# install.ps1
# Installs Activity Tracker service, session agent task, and sets up ProgramData ACLs.
# Must be run as Administrator.
#
# Parameters:
#   -InstallDir  Path where the application files are installed (e.g. C:\Program Files\ActivityTracker)
#   -UserName    The Windows username whose activity will be tracked (e.g. DESKTOP-ABC\Alice)

param(
    [Parameter(Mandatory = $true)]
    [string]$InstallDir,

    [Parameter(Mandatory = $true)]
    [string]$UserName
)

$ErrorActionPreference = "Stop"

# ── Logging setup ──
$programData = "$env:ProgramData\ActivityTracker"
if (-not (Test-Path $programData)) {
    New-Item -ItemType Directory -Path $programData -Force | Out-Null
}
$logDir = Join-Path $programData "logs"
if (-not (Test-Path $logDir)) {
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null
}
$logFile = Join-Path $logDir "install.log"

function Write-Log($message) {
    $ts = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $line = "[$ts] $message"
    Write-Host $line
    Add-Content -Path $logFile -Value $line -ErrorAction SilentlyContinue
}

function Fail-Step($stepName, $detail) {
    Write-Log "FAILED at step: $stepName - $detail"
    Write-Host ""
    Write-Error "FAILED at step: $stepName - $detail"
    exit 1
}

Write-Log "========================================="
Write-Log "Activity Tracker Install started"
Write-Log "InstallDir: $InstallDir"
Write-Log "UserName:   $UserName"
Write-Log "========================================="

# ── Step 1: Check for Administrator ──
Write-Log "Step 1: Checking Administrator privileges..."
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]$identity
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Fail-Step "AdminCheck" "This script must be run as Administrator. Please re-run from an elevated prompt."
}

# ── Step 2: Verify binary paths ──
Write-Log "Step 2: Checking executable paths..."
$serviceExe = Join-Path $InstallDir "service\ActivityTracker.Service.exe"
$agentExe   = Join-Path $InstallDir "agent\ActivityTracker.SessionAgent.exe"

if (-not (Test-Path $serviceExe)) {
    Fail-Step "VerifyServiceExe" "Service executable not found at: $serviceExe"
}
if (-not (Test-Path $agentExe)) {
    Fail-Step "VerifyAgentExe" "SessionAgent executable not found at: $agentExe"
}

# ── Step 3: Create ProgramData folder and set ACL ──
Write-Log "Step 3: Creating ProgramData folder and setting ACL..."
if (-not (Test-Path $programData)) {
    New-Item -ItemType Directory -Path $programData -Force | Out-Null
}

& icacls "$programData" /inheritance:r /grant:r "SYSTEM:(OI)(CI)F" "Administrators:(OI)(CI)F" "${UserName}:(OI)(CI)M"
if ($LASTEXITCODE -ne 0) {
    Fail-Step "ApplyFolderAcl" "Failed to apply folder ACL using icacls."
}
Write-Log "ACL applied: SYSTEM:F, Administrators:F, ${UserName}:M"

# ── Step 4: Stop and delete existing service ──
Write-Log "Step 4: Ensuring clean service state..."
$serviceName = "ActivityTrackerService"
$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Log "Stopping existing $serviceName..."
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
        Fail-Step "StopExistingService" "$serviceName did not reach STOPPED state within 30 seconds."
    }
    Write-Log "Deleting existing $serviceName..."
    & sc.exe delete $serviceName | Out-Null
    Start-Sleep -Seconds 2
}

# ── Step 4b: Kill any running SessionAgent processes ──
Write-Log "Stopping any running ActivityTracker.SessionAgent processes..."
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

# ── Step 4c: Remove existing scheduled task ──
if ($existingTask) {
    Write-Log "Removing existing scheduled task $taskName..."
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
}

# ── Step 5: Create service ──
Write-Log "Step 5: Creating $serviceName..."
& sc.exe create $serviceName binPath= "`"$serviceExe`"" start= auto
if ($LASTEXITCODE -ne 0) {
    Fail-Step "CreateService" "sc.exe create failed."
}

# ── Step 6: Set description ──
Write-Log "Step 6: Setting service description..."
& sc.exe description $serviceName "Local Activity Tracker Service for monitoring active windows and browser domains."
if ($LASTEXITCODE -ne 0) {
    Fail-Step "SetDescription" "sc.exe description failed."
}

# ── Step 7: Set failure recovery policy ──
Write-Log "Step 7: Configuring failure recovery policy..."
& sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/5000/restart/60000
if ($LASTEXITCODE -ne 0) {
    Fail-Step "SetFailureActions" "sc.exe failure failed."
}

# ── Step 8: Register SessionAgent scheduled task ──
Write-Log "Step 8: Registering SessionAgent scheduled task for $UserName..."
$action    = New-ScheduledTaskAction -Execute $agentExe
$trigger   = New-ScheduledTaskTrigger -AtLogOn -User $UserName
$settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) `
    -MultipleInstances IgnoreNew
$taskPrincipal = New-ScheduledTaskPrincipal -UserId $UserName -LogonType Interactive -RunLevel Limited

Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger `
    -Settings $settings -Principal $taskPrincipal | Out-Null
Write-Log "Scheduled task $taskName registered."

# ── Step 9: Start service and SessionAgent ──
Write-Log "Step 9: Starting service and SessionAgent..."
& sc.exe start $serviceName
if ($LASTEXITCODE -ne 0) {
    Fail-Step "StartService" "sc.exe start failed."
}

Start-ScheduledTask -TaskName $taskName
Start-Sleep -Seconds 3

# ── Step 10: Verify service running and HTTP endpoint ──
Write-Log "Step 10: Verifying service and HTTP endpoint..."
$queryOutput = & sc.exe query $serviceName
if (($queryOutput -join "`n") -notmatch "STATE\s*:\s*\d+\s+RUNNING") {
    Fail-Step "VerifyServiceStatus" "Service is not in RUNNING state. sc query output:`n$($queryOutput -join "`n")"
}

$httpOk = $false
for ($attempt = 0; $attempt -lt 10; $attempt++) {
    try {
        $config = Invoke-RestMethod -Uri "http://127.0.0.1:14321/config" -Method Get -TimeoutSec 5
        if ($config -and $config.HttpPort) {
            $httpOk = $true
            break
        }
    }
    catch {
        Write-Log "HTTP check attempt $($attempt + 1) failed: $_"
    }
    Start-Sleep -Seconds 2
}
if (-not $httpOk) {
    Fail-Step "VerifyHttpEndpoint" "Failed to connect to http://127.0.0.1:14321/config after 20 seconds. Check $logDir\service.log for details."
}

Write-Log ""
Write-Log "SUCCESS: ActivityTrackerService is RUNNING and responding to HTTP requests."
Write-Log "SessionAgent is registered and running under user '$UserName'."
Write-Host ""
Write-Host "SUCCESS: ActivityTrackerService is RUNNING and responding to HTTP requests." -ForegroundColor Green
Write-Host "SessionAgent is registered and running under user '$UserName'." -ForegroundColor Green
exit 0
