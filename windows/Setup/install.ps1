#Requires -RunAsAdministrator
# Install SteamTimer: service (SYSTEM) + tray app autostart + watchdog.
$ErrorActionPreference = "Stop"
$Src = Split-Path $PSScriptRoot -Parent
$Dest = "C:\Program Files\SteamTimer"
$Stage = Join-Path ([System.IO.Path]::GetTempPath()) ("SteamTimer-" + [guid]::NewGuid())
$Watchdog = Get-ScheduledTask -TaskName "SteamTimerWatchdog" -ErrorAction SilentlyContinue

function Invoke-Native {
    param([string]$File, [string[]]$Arguments)
    # Pass escaped quotes in the service image path the same way in both PowerShell versions.
    if ($File -eq 'sc.exe') { $PSNativeCommandArgumentPassing = 'Legacy' }
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$File failed with exit code $LASTEXITCODE" }
}

try {
    Write-Host "Publishing..."
    # Stage both successful builds before stopping or changing the installed app.
    Invoke-Native -File dotnet -Arguments @("publish", "$Src\Service\Service.csproj", "-c", "Release", "-r", "win-x64", "--self-contained", "false", "-o", "$Stage\Service")
    Invoke-Native -File dotnet -Arguments @("publish", "$Src\App\App.csproj", "-c", "Release", "-r", "win-x64", "--self-contained", "false", "-o", "$Stage\App")

    if (-not (Test-Path (Join-Path $Stage "App\App.pri"))) { throw "Published app is missing App.pri; installation has not been changed." }

    Write-Host "Stopping old version..."
    if ($Watchdog) {
        Disable-ScheduledTask -TaskName "SteamTimerWatchdog" | Out-Null
        Stop-ScheduledTask -TaskName "SteamTimerWatchdog"
    }
    $Service = Get-Service -Name SteamTimer -ErrorAction SilentlyContinue
    if ($Service -and $Service.Status -ne 'Stopped') {
        $Service.Stop()
        try { $Service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20)) }
        catch [System.ServiceProcess.TimeoutException] {
            Get-Process -Name Service -ErrorAction SilentlyContinue |
                Where-Object { $_.Path -eq "$Dest\Service.exe" } | Stop-Process -Force
            $Service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(10))
        }
    }
    Get-Process -Name App -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq "$Dest\App\App.exe" } | Stop-Process -Force

    Write-Host "Copying to $Dest ..."
    New-Item -ItemType Directory -Path $Dest -Force | Out-Null
    Get-ChildItem -LiteralPath "$Stage\Service" |
        Where-Object { $_.Name -ne 'appsettings.json' -or -not (Test-Path -LiteralPath "$Dest\appsettings.json") } |
        Copy-Item -Destination "$Dest\" -Recurse -Force
    New-Item -ItemType Directory -Path "$Dest\App" -Force | Out-Null
    Copy-Item "$Stage\App\*" "$Dest\App\" -Recurse -Force

    Write-Host "Configuring service..."
    # Update in place: deleting/recreating a service races pending deletion.
    $ServiceCommand = if ($Service) { "config" } else { "create" }
    $OwnerSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $ServicePath = '\"{0}\Service.exe\" --Pipe:AllowedUserSid={1}' -f $Dest, $OwnerSid
    Invoke-Native -File sc.exe -Arguments @($ServiceCommand, "SteamTimer", "binPath=", $ServicePath, "start=", "auto", "obj=", "LocalSystem")
    Invoke-Native -File sc.exe -Arguments @("description", "SteamTimer", "Enforces daily Steam time limit; resets at 00:00 UTC-05.")
    Invoke-Native -File sc.exe -Arguments @("failure", "SteamTimer", "reset=", "86400", "actions=", "restart/60000/restart/60000/restart/60000")
    Start-Service SteamTimer
    (Get-Service SteamTimer).WaitForStatus('Running', [TimeSpan]::FromSeconds(20))

    Write-Host "Registering tray autostart and watchdog..."
    $AppExe = "$Dest\App\App.exe"
    New-Item -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Force | Out-Null
    Set-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "SteamTimer" -Value "`"$AppExe`""
    $Action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument "-NoProfile -WindowStyle Hidden -Command `"if ((Get-Service SteamTimer).Status -ne 'Running') { Start-Service SteamTimer }`""
    $Trigger = New-ScheduledTaskTrigger -Once -At (Get-Date) -RepetitionInterval (New-TimeSpan -Minutes 5)
    Register-ScheduledTask -TaskName "SteamTimerWatchdog" -Action $Action -Trigger $Trigger -User "SYSTEM" -RunLevel Highest -Force | Out-Null

    $Wsh = New-Object -ComObject WScript.Shell
    $Lnk = $Wsh.CreateShortcut("$env:ProgramData\Microsoft\Windows\Start Menu\Programs\Steam Timer.lnk")
    $Lnk.TargetPath = $AppExe
    $Lnk.Save()
    Start-Process $AppExe
    Write-Host "Done. Open 'Steam Timer' from Start. Sessions, cooldowns and daily/weekly budgets are tracked automatically."
}
finally {
    if (Test-Path $Stage) { Remove-Item $Stage -Recurse -Force }
    if ($Watchdog) { Enable-ScheduledTask -TaskName "SteamTimerWatchdog" -ErrorAction SilentlyContinue | Out-Null }
}
