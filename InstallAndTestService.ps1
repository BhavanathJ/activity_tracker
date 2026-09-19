# Run this script in an Administrative PowerShell prompt to register, start and test the service
$exePath = Join-Path $PSScriptRoot "ActivityTracker.Service\bin\Release\net8.0-windows\ActivityTracker.Service.exe"
$agentPath = Join-Path $PSScriptRoot "ActivityTracker.SessionAgent\bin\Release\net8.0-windows\win-x64\publish\ActivityTracker.SessionAgent.exe"

# 1. Register the service
sc.exe create ActivityTrackerService binPath= "`"$exePath`"" start= auto

# 2. Start the service
sc.exe start ActivityTrackerService

# 3. Register the SessionAgent as a Scheduled Task (runs in the interactive user session)
$existingTask = Get-ScheduledTask -TaskName "ActivityTrackerSessionAgent" -ErrorAction SilentlyContinue
if ($existingTask) {
    Unregister-ScheduledTask -TaskName "ActivityTrackerSessionAgent" -Confirm:$false
}

$action   = New-ScheduledTaskAction -Execute $agentPath
$trigger  = New-ScheduledTaskTrigger -AtLogOn
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero)
Register-ScheduledTask -TaskName "ActivityTrackerSessionAgent" -Action $action -Trigger $trigger -Settings $settings -RunLevel Limited

Write-Host "Service installed and started."
Write-Host "SessionAgent registered as a Scheduled Task (will start at next logon, or start manually with: Start-ScheduledTask -TaskName ActivityTrackerSessionAgent)."
Write-Host "Please lock and unlock your screen (Win+L), then check your logs or database to confirm events are captured."
