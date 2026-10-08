# Registers "WCS Trips flights" in Windows Task Scheduler: scripts\flights-home.ps1 every day at
# 08:00, run as soon as possible after a missed start (PC off at 08:00), only while you are signed in.
# Remove it with: Unregister-ScheduledTask -TaskName "WCS Trips flights"

$script = Join-Path $PSScriptRoot "flights-home.ps1"
$action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$script`""
$trigger = New-ScheduledTaskTrigger -Daily -At 08:00
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -RunOnlyIfNetworkAvailable -ExecutionTimeLimit (New-TimeSpan -Hours 1)

Register-ScheduledTask -TaskName "WCS Trips flights" -Action $action -Trigger $trigger -Settings $settings -Description "Daily Ryanair and Wizz Air fares for WCS Trips (Wizz refuses the cloud job)." -Force
