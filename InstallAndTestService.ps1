# InstallAndTestService.ps1
# Requires Administrator privileges to register and configure Windows Services and ACLs.

$ErrorActionPreference = "Stop"

function Fail-Step($stepName, $detail) {
    Write-Host ""
    Write-Error "FAILED at step: $stepName - $detail"
    exit 1
}

# 1. Check for Administrator
Write-Host "Step 1: Checking Administrator privileges..."
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]$identity
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Fail-Step "AdminCheck" "This script must be run as Administrator. Please re-run from an elevated PowerShell prompt."
}

# 2. Check binary paths
Write-Host "Step 2: Checking executable paths..."
$serviceExe = Join-Path $PSScriptRoot "ActivityTracker.Service\bin\Release\net8.0-windows\win-x64\publish\ActivityTracker.Service.exe"
$agentExe = Join-Path $PSScriptRoot "ActivityTracker.SessionAgent\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\ActivityTracker.SessionAgent.exe"
if (-not (Test-Path $agentExe)) {
    $fallbackAgent = Join-Path $PSScriptRoot "ActivityTracker.SessionAgent\bin\Release\net8.0-windows\win-x64\publish\ActivityTracker.SessionAgent.exe"
    if (Test-Path $fallbackAgent) { $agentExe = $fallbackAgent }
}

if (-not (Test-Path $serviceExe)) {
    Fail-Step "VerifyServiceExe" "Service executable not found at: $serviceExe. Please build the project with dotnet publish first."
}
if (-not (Test-Path $agentExe)) {
    Fail-Step "VerifyAgentExe" "SessionAgent executable not found at: $agentExe. Please build the project with dotnet publish first."
}

# 3. Apply folder ACL
Write-Host "Step 3: Creating ProgramData folder and setting ACL..."
$programData = "$env:ProgramData\ActivityTracker"
if (-not (Test-Path $programData)) {
    New-Item -ItemType Directory -Path $programData -Force | Out-Null
}
$currentUser = $env:USERNAME
& icacls "$programData" /inheritance:r /grant:r "SYSTEM:(OI)(CI)F" "Administrators:(OI)(CI)F" "${currentUser}:(OI)(CI)M"
if ($LASTEXITCODE -ne 0) {
    Fail-Step "ApplyFolderAcl" "Failed to apply folder ACL using icacls."
}

# 4. Stop and delete existing service (idempotent)
Write-Host "Step 4: Ensuring clean service state (stopping and deleting any existing service)..."
$serviceName = "ActivityTrackerService"
$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Stopping existing $serviceName..."
    & sc.exe stop $serviceName | Out-Null
    $stopped = $false
    for ($i = 0; $i -lt 30; $i++) {
        $q = & sc.exe query $serviceName
        if ($q -match "STATE\s*:\s*\d+\s+STOPPED") {
            $stopped = $true
            break
        }
        Start-Sleep -Seconds 1
    }
    if (-not $stopped) {
        Fail-Step "StopExistingService" "$serviceName did not reach STOPPED state within 30 seconds."
    }
    Write-Host "Deleting existing $serviceName..."
    & sc.exe delete $serviceName | Out-Null
    Start-Sleep -Seconds 2
}

# 5. Create service
Write-Host "Step 5: Creating $serviceName..."
& sc.exe create $serviceName binPath= "`"$serviceExe`"" start= auto
if ($LASTEXITCODE -ne 0) {
    Fail-Step "CreateService" "sc.exe create failed."
}

# 6. Set description
Write-Host "Step 6: Setting service description..."
& sc.exe description $serviceName "Local Activity Tracker Service for monitoring active windows and browser domains."
if ($LASTEXITCODE -ne 0) {
    Fail-Step "SetDescription" "sc.exe description failed."
}

# 7. Set failure recovery policy
Write-Host "Step 7: Configuring failure recovery policy..."
& sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/5000/restart/60000
if ($LASTEXITCODE -ne 0) {
    Fail-Step "SetFailureActions" "sc.exe failure failed."
}

# Before step 8: Stop existing SessionAgent scheduled task and processes
$taskName = "ActivityTrackerSessionAgent"
$existingTask = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if ($existingTask) {
    Write-Host "Stopping existing scheduled task $taskName..."
    Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
}

Write-Host "Stopping any running ActivityTracker.SessionAgent processes..."
Get-Process ActivityTracker.SessionAgent -EA SilentlyContinue | Stop-Process -Force
for ($i = 0; $i -lt 10; $i++) {
    if (-not (Get-Process ActivityTracker.SessionAgent -EA SilentlyContinue)) {
        break
    }
    Start-Sleep -Seconds 1
}

# 8. Register SessionAgent scheduled task for current user
Write-Host "Step 8: Registering SessionAgent scheduled task..."
if ($existingTask) {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
}

$action = New-ScheduledTaskAction -Execute $agentExe
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $identity.Name
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero)
$principal = New-ScheduledTaskPrincipal -UserId $identity.Name -LogonType Interactive -RunLevel Limited

Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal | Out-Null

# 9. Start service and SessionAgent
Write-Host "Step 9: Starting service and SessionAgent..."
& sc.exe start $serviceName
if ($LASTEXITCODE -ne 0) {
    Fail-Step "StartService" "sc.exe start failed."
}

Start-ScheduledTask -TaskName $taskName
Start-Sleep -Seconds 3

# 10. Verify service status and HTTP GET /config
Write-Host "Step 10: Verifying service and HTTP endpoint..."
$queryOutput = & sc.exe query $serviceName
if (($queryOutput -join "`n") -notmatch "STATE\s*:\s*\d+\s+RUNNING") {
    Fail-Step "VerifyServiceStatus" "Service is not in RUNNING state. sc query output: `n$($queryOutput -join "`n")"
}

try {
    $config = Invoke-RestMethod -Uri "http://127.0.0.1:14321/config" -Method Get -TimeoutSec 10
    if (-not $config -or -not $config.HttpPort) {
        Fail-Step "VerifyHttpEndpoint" "GET http://127.0.0.1:14321/config returned an empty or invalid response."
    }
}
catch {
    Fail-Step "VerifyHttpEndpoint" "Failed to connect to http://127.0.0.1:14321/config: $_"
}

Write-Host ""
Write-Host "SUCCESS: ActivityTrackerService is RUNNING and responding to HTTP requests." -ForegroundColor Green
Write-Host "SessionAgent is registered and running under user '$($identity.Name)'." -ForegroundColor Green
