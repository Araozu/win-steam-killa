#Requires -RunAsAdministrator
# Install SteamTimer: service (SYSTEM) + tray app autostart + watchdog.
$ErrorActionPreference = "Stop"
$Src = Split-Path $PSScriptRoot -Parent
$Dest = "C:\Program Files\SteamTimer"

Write-Host "Publishing..."
& dotnet publish "$Src\Service\Service.csproj" -c Release -r win-x64 --self-contained false -o "$Src\publish\Service" | Out-Null
& dotnet publish "$Src\App\App.csproj" -c Release -r win-x64 --self-contained false -o "$Src\publish\App" | Out-Null

Write-Host "Stopping old version (service holds its files while running)..."
sc.exe stop SteamTimer 2>$null | Out-Null
# Tray app also runs from Program Files, so close it before overwriting.
Get-Process -Name App -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -like "$Dest*" } | Stop-Process -Force -ErrorAction SilentlyContinue
# Wait until Service.exe really exits (max ~20s), then force-kill leftovers.
$deadline = (Get-Date).AddSeconds(20)
while ((Get-Date) -lt $deadline) {
    $p = Get-Process -Name Service -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -like "$Dest*" }
    if (-not $p) { break }
    Start-Sleep -Milliseconds 500
}
Get-Process -Name Service -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -like "$Dest*" } | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1
sc.exe delete SteamTimer 2>$null | Out-Null
Start-Sleep -Seconds 2

Write-Host "Copying to $Dest ..."
New-Item -ItemType Directory -Path $Dest -Force | Out-Null
Copy-Item "$Src\publish\Service\*" "$Dest\" -Recurse -Force
New-Item -ItemType Directory -Path "$Dest\App" -Force | Out-Null
Copy-Item "$Src\publish\App\*" "$Dest\App\" -Recurse -Force

Write-Host "Creating service..."
sc.exe create SteamTimer binPath= "`"$Dest\Service.exe`"" start= auto obj= LocalSystem | Out-Null
sc.exe description SteamTimer "Enforces daily Steam time limit; resets at 03:00." | Out-Null
# Restart on crash: 3 retries, 60s delay, reset counter daily
sc.exe failure SteamTimer reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
sc.exe start SteamTimer | Out-Null

Write-Host "Registering tray autostart (current user + watchdog)..."
$AppExe = "$Dest\App\App.exe"
# Run key for tray on login
New-Item -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Force | Out-Null
Set-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "SteamTimer" -Value "`"$AppExe`""
# Watchdog: every 5 min ensure service running (SYSTEM)
$Action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument "-NoProfile -WindowStyle Hidden -Command `"if ((Get-Service SteamTimer).Status -ne 'Running') { Start-Service SteamTimer }`""
$Trigger = New-ScheduledTaskTrigger -Once -At (Get-Date) -RepetitionInterval (New-TimeSpan -Minutes 5)
Register-ScheduledTask -TaskName "SteamTimerWatchdog" -Action $Action -Trigger $Trigger -User "SYSTEM" -RunLevel Highest -Force | Out-Null

# Start Menu shortcut
$Wsh = New-Object -ComObject WScript.Shell
$Lnk = $Wsh.CreateShortcut("$env:ProgramData\Microsoft\Windows\Start Menu\Programs\Steam Timer.lnk")
$Lnk.TargetPath = $AppExe
$Lnk.Save()

Write-Host "Starting tray app..."
Start-Process $AppExe

Write-Host "Done. Open 'Steam Timer' from Start. Auto-tracked: 2h sessions + len-based cooldowns, M-F 1h30 / Sat 2h30 / Sun 4h +30m carry, Dota 8h/week (forbidden Mon/Wed/Fri)."
