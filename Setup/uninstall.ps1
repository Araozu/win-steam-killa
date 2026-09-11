#Requires -RunAsAdministrator
$ErrorActionPreference = "Continue"
sc.exe stop SteamTimer 2>$null | Out-Null
sc.exe delete SteamTimer 2>$null | Out-Null
Unregister-ScheduledTask -TaskName "SteamTimerWatchdog" -Confirm:$false -ErrorAction SilentlyContinue
Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "SteamTimer" -ErrorAction SilentlyContinue
Remove-Item "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\Steam Timer.lnk" -Force -ErrorAction SilentlyContinue
Get-Process -Name App -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "*SteamTimer*" } | Stop-Process -Force
Write-Host "Uninstalled. Config left at C:\ProgramData\SteamTimer (delete it to fully reset today's timer)."
