#Requires -RunAsAdministrator
$ErrorActionPreference = "Stop"
# Remove the watchdog first so it cannot restart the service during removal.
Unregister-ScheduledTask -TaskName "SteamTimerWatchdog" -Confirm:$false -ErrorAction SilentlyContinue
$Service = Get-Service -Name SteamTimer -ErrorAction SilentlyContinue
if ($Service) {
    if ($Service.Status -ne 'Stopped') {
        $Service.Stop()
        $Service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    & sc.exe delete SteamTimer
    if ($LASTEXITCODE -ne 0) { throw "Could not remove SteamTimer service (exit $LASTEXITCODE)." }
}
Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "SteamTimer" -ErrorAction SilentlyContinue
Remove-Item "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\Steam Timer.lnk" -Force -ErrorAction SilentlyContinue
Get-Process -Name App -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq "C:\Program Files\SteamTimer\App\App.exe" } | Stop-Process -Force
Write-Host "Uninstalled. Installed files and usage history have been retained."
