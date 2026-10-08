# Registers "WCS Trips flights" in Windows Task Scheduler: scripts\flights-home.ps1 every day at
# 08:00, run as soon as possible after a missed start (PC off at 08:00), only while you are signed in.
# Remove it with: Unregister-ScheduledTask -TaskName "WCS Trips flights"

$script = Join-Path $PSScriptRoot "flights-home.ps1"
# conhost --headless: no console window at all, so nothing on screen to close by accident (closing
# one stops the run). -WindowStyle Hidden alone still flashes a window that can stay open.
$action = New-ScheduledTaskAction -Execute "conhost.exe" -Argument "--headless powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$script`""
$trigger = New-ScheduledTaskTrigger -Daily -At 08:00
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -RunOnlyIfNetworkAvailable -ExecutionTimeLimit (New-TimeSpan -Hours 1)

Register-ScheduledTask -TaskName "WCS Trips flights" -Action $action -Trigger $trigger -Settings $settings -Description "Daily Ryanair and Wizz Air fares for WCS Trips (Wizz refuses the cloud job)." -Force
