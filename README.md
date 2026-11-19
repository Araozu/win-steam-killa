# SteamTimer

Self-control Steam limiter. Fully automatic — nothing to start or type,
just play and it tracks. Day resets at **00:00 UTC-05**. Survives reboot.
After the budget is used, kills Steam + games and keeps enforcing until 00:00.

Every rule runs on a fixed **UTC-05** clock: day and week resets, play
windows and the times shown in the app. The PC's time zone does not matter.

## Rules (hard-coded in `Shared/Limits.cs`, info-only in the UI)

- **Daily budget:** Mon–Fri **1h30m**, Sat **2h30m**, Sun **4h**.
  Up to **30m** of unused budget carries over to the next day.
- **Borrow from tomorrow:** button takes **10m** off tomorrow for up to
  **+9m30s today** (95% on the 1st press, −7pp, −8pp, −9pp... each further press, 0.1%
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
- **Dota 2:** **workout-gated on weekdays, up to 8h weekly**, resets
  **Monday 00:00**. On Mon–Fri, the base allowance comes from cached workout
  credit (up to 8h/week); existing Dota borrowing adds to that allowance.
  The Windows service imports Health Connect workouts and earns 3.3 seconds
  of Dota credit per workout second. Overlapping workouts count their shared
  time once. Fresh state starts with zero workout credit. **Sat/Sun keep the old free
  grant** (up to 8h/week, no workout needed). Dota time burns both budgets,
  but a Dota-only lockout kills just Dota — Steam and other games continue
  on the daily budget. **Forbidden on Mon / Wed / Fri** — Dota is
  closed/blocked all those days (00:00–00:00), burns no weekly budget.
- **Play hours:** Mon–Fri Steam may only run **05:30–06:15** and **20:15–22:00**.
  Outside those windows Steam + games are closed/blocked and nothing accrues.
  **Sat/Sun have no time restriction** (budget/cooldown/Dota rules still apply).
- **Borrow Dota from next week:** button takes **10m** off next week's Dota
  for up to **+9m30s this week** (95% on the 1st press, −7pp, −8pp, −9pp...
  each further press, 50% floor). One click, no confirm. Repeatable, capped
  so next week never drops below zero.

## Use (you need nothing about Windows)

1. Install the .NET 10 SDK and .NET 10 Desktop Runtime on Windows.
2. Open PowerShell **as Administrator** from the Windows account that will use
   the tray, go to this repository, and run `./Setup/install.ps1`. The installer
   grants that account access to the service's named pipe.
3. Open **Steam Timer** from Start to see budget / session / cooldown / Dota.
   The tray icon quietly shows remaining time. 15 min before the budget,
   session, weekday play window or Dota allowance ends: warning.
   At deadline: Steam is closed + blocked until 00:00.
   Closing the window does NOT stop the timer.

## How it works

- `Service/` — Windows Service as LocalSystem. Owns state
  (`C:\ProgramData\SteamTimer\state.json`), 15s tick, 15m warning, deadline kill,
  60s enforce loop until 00:00. A separate worker syncs workouts at startup and
  every 15 minutes, then calculates credit locally. Failed syncs preserve cached
  credit. The workout archive keeps 90 days of records and a backup copy.
  The app connects over the `steamtimer` named pipe. Open **Workout sync**
  in the app to configure the receiver and run a sync on demand.
  [Workout sync setup](WORKOUT_PIPELINE.md#local-sync-and-retention)
- `App/` — WinUI 3 Fluent window + quiet WinForms tray (`NotifyIcon` tooltip = countdown,
  balloons only for warnings/kill/cooldown/Dota).
- `Shared/` — `Limits` (2h session max, len-based cooldown, 30m carryover,
  workout-gated Dota up to 8h/week), `PolicyClock` (fixed UTC-05),
  `LogicalDay` (00:00 reset), pipe DTOs.

## Dev

Install .NET 11 SDK to build the complete solution (WorkoutService targets .NET 11;
App/Service/Shared target .NET 10). The WinUI app requires Windows to build and run. The service also requires Windows to run.

```sh
dotnet build SteamTimer.slnx -c Release
dotnet test Tests/Tests.csproj -c Release --no-build
pwsh -NoProfile -File Tests/VerifySetup.ps1
```

On Linux or macOS, build `Tests/Tests.csproj` instead of the full solution before running the tests. The WinUI app uses the self-contained Windows App SDK and targets Windows 10 version 1809 or later.

The tests run the production timer engine with a simulated clock/process list,
and exercise raw capture, authorization, credit-client failures and caching. CI runs
builds, tests, and setup-script checks on Linux and Windows. See `REVIEW.md` for
fixes and remaining Windows runtime checks.

`WorkoutService/` receives workouts from **Simple Health → Health Connect →
Health Connect Webhook**. `POST /api/capture` saves uploads for inspection.
[WORKOUT_PIPELINE.md](WORKOUT_PIPELINE.md) covers phone setup, the observed
workout fields, PC acknowledgements, and server retention.
The PC calculates credit from its deduplicated archive for the current logical
week. Fresh state starts at zero.

## Notes / limits

- Self-control grade: you are Admin, so you *could* `sc stop SteamTimer` or
  delete state. It just makes it annoying by accident, which is the point.
- Steam + child processes are killed (client, helpers, overlay, launched games).
  Dota lockouts kill only `dota2` processes.
- The service logs observed Steam child games (exe name + path + time) in
  state for future precise per-game tracking.
- Uninstall with `Setup/uninstall.ps1` as admin; usage history is retained.
- State writes retain a backup. A corrupt primary is recovered from that backup;
  if both are invalid, service startup fails instead of silently clearing usage.
