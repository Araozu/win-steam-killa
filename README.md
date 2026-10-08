# SteamTimer

Self-control Steam limiter. Fully automatic — nothing to start or type,
just play and it tracks. Day resets at **03:00 local**. Survives reboot.
After the budget is used, kills Steam + games and keeps enforcing until 03:00.

## Rules (hard-coded in `Shared/Limits.cs`, info-only in the UI)

- **Daily budget:** Mon–Fri **1h30m**, Sat **2h30m**, Sun **4h**.
  Up to **30m** of unused budget carries over to the next day.
- **Borrow from tomorrow:** button takes **10m** off tomorrow for up to
  **+9m30s today** (95% on the 1st press, −7pp each further press, 0.1%
  floor). One click, no confirm. Repeatable, capped so tomorrow never drops
  below zero. **Unavailable on Sundays.** The button heats toward red as
  effectiveness drops. Pressing mid-session extends the current session by the
  gained amount (the 2h session max still applies). Yesterday's debt shows as
  "− advance" in the Today line; cooldowns still apply.
- **Sessions:** each session can use up to **2h** of the day's budget.
  A session starts the moment Steam is seen and ends when Steam closes
  (or the 2h max / budget hits).
- **Cooldown:** after *every* session, Steam is blocked for that session's
  length **rounded up to 5 minutes** (e.g. 1h12m played → 1h15m cooldown).
- **Dota 2:** **9h weekly** budget, resets **Monday 03:00**. Dota time burns
  both budgets, but a Dota-only lockout kills just Dota — Steam and other
  games continue on the daily budget.

## Use (you need nothing about Windows)

1. Right-click `Setup/install.ps1` → **Run with PowerShell as Administrator**.
2. Open **Steam Timer** from Start to see budget / session / cooldown / Dota.
   The tray icon quietly shows remaining time. At 15 min left: warning.
   At deadline: Steam is closed + blocked until 03:00.
   Closing the window does NOT stop the timer.

## How it works

- `Service/` — Windows Service as LocalSystem. Owns state
  (`C:\ProgramData\SteamTimer\state.json`), 15s tick, 15m warning, deadline kill,
  60s enforce loop until 03:00. Named pipe `steamtimer` (`status`; the old
  manual `start` now just answers "tracking is automatic").
- `App/` — WinForms info window + quiet tray (`NotifyIcon` tooltip = countdown,
  balloons only for warnings/kill/cooldown/Dota).
- `Shared/` — `Limits` (2h session max, len-based cooldown, 30m carryover,
  9h Dota week), `LogicalDay` (03:00 reset), pipe DTOs.

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
  Dota lockouts kill only `dota2` processes.
- The service logs observed Steam child games (exe name + path + time) in
  state for future precise per-game tracking.
- To fully reset: `Setup/uninstall.ps1` as admin, then delete
  `C:\ProgramData\SteamTimer`.
