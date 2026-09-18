# Run this script in an Administrative PowerShell prompt to register, start and test the service
$exePath = "c:\My Files\Project\Activity Tracker\ActivityTracker.Service\bin\Release\net8.0-windows\ActivityTracker.Service.exe"

# 1. Register the service
sc.exe create ActivityTrackerService binPath= "`"$exePath`"" start= auto

# 2. Start the service
sc.exe start ActivityTrackerService

Write-Host "Service installed and started. Please lock and unlock your screen (Win+L), then check your logs or database to confirm the lock event was captured via OnSessionChange."
