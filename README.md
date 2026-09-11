# SteamTimer

Self-control Steam limiter. One timer per day, max **3h** (hard-coded in `Shared/Limits.cs`).
Day resets at **03:00 local**. Survives reboot. After deadline, kills Steam + games
and keeps enforcing until 03:00.

## Use (you need nothing about Windows)

1. Right-click `Setup/install.ps1` → **Run with PowerShell as Administrator**.
2. Open **Steam Timer** from Start → type `1h`, `1h30m`, `2h` (or click a preset) → Start.
3. Tray icon quietly shows remaining time. At 15 min left: warning. At deadline:
   Steam is closed + blocked until 03:00. Closing the window does NOT stop the timer.

## How it works

- `Service/` — Windows Service as LocalSystem. Owns state
  (`C:\ProgramData\SteamTimer\state.json`), 15s tick, 15m warning, deadline kill,
  60s enforce loop until 03:00. Named pipe `steamtimer` with `start`/`status`.
- `App/` — WinForms prompt + quiet tray (`NotifyIcon` tooltip = countdown,
  balloons only for warning/kill). No Stop button by design.
- `Shared/` — `Limits` (3h cap), `DurationParser`, `LogicalDay` (03:00 reset), pipe DTOs.

## Dev

```powershell
dotnet build SteamTimer.slnx
```
Requires .NET 10 SDK + Windows. Service must run on Windows
(EventLog, Toolhelp32, named pipes).

## Notes / limits

- Self-control grade: you are Admin, so you *could* `sc stop SteamTimer` or
  delete state. It just makes it annoying by accident, which is the point.
- Steam + child processes are killed (client, helpers, overlay, launched games).
- To fully reset: `Setup/uninstall.ps1` as admin, then delete
  `C:\ProgramData\SteamTimer`.
