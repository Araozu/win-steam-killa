# Current review notes

See `REVIEW.md` for the current implementation and verification, and `RESETS.md`
for the wipe history. The notes below are historical; state is v7
(weekday workout-gated Dota: Mon–Fri playable Dota = min(8h cap, earned)
+ borrows − deductions, fail-closed at 0; Sat/Sun keep the free 8h grant).
Strava has been removed. WorkoutService is now a raw Notify/Tasker capture demo;
credit mapping waits for hardware samples. See `WORKOUT_PIPELINE.md` for the
current plan and setup, including the Tasker export probe and source-selection
gate. Spreadsheet export is documented for sensor data, not confirmed workout
records. The Windows credit consumer still polls independently
every 15 minutes, but capture-only HTTP 503 responses preserve cached credit.

---

# SteamTimer — agent handover

## What this is
Self-control Steam limiter for Windows. Fully automatic (no manual timer):
each logical day (03:00 local) has a base budget (M-F 1h30m, Sat 2h30m,
Sun 4h) plus up to 30m leftover carried from the previous day. Sessions
auto-start when Steam is seen, max 2h of daily budget each; after EVERY
session a cooldown blocks Steam equal to that session's length rounded up to
5 minutes. Warns at T-15m, kills Steam + launched games at deadline, keeps
enforcing until 03:00. Survives reboot.
Dota 2 has a separate 9h weekly budget (Mon 03:00 reset): Dota burns both
budgets, but Dota exhaustion kills ONLY Dota — other games continue.
Self-control grade (user keeps Admin): friction, not fortress.

## Layout (`C:\Users\Fernando\proj\steam-timer\`, git repo, `main`, no remote)
- `Shared/` — `Limits.cs` (2h session max, 5m cooldown quantum, 30m carryover,
   9h Dota week, 03:00 reset, pipe name, state path, 10m/95%/-7pp/0.1% borrow),
  `LogicalDay.cs`,
  `TimerState.cs` (v3 state + `GameRecord` identity log + pipe DTOs + borrow/
  advance fields + `TomorrowBaseSeconds` for the cap).
  (`DurationParser.cs` was deleted with the manual-start UI.)
- `Service/` — .NET 10 Worker, runs as **LocalSystem** (`AddWindowsService`,
  name `SteamTimer`). `TimerEngine` owns state, `Worker` ticks every 15s,
  `Killer` kills by name + descendant-of-Steam tree (Toolhelp32 parent PIDs,
  no NuGet) + Dota-only kill + single-snapshot play/Dota/game-identity view,
  `PipeServer` = JSON-lines over named pipe `steamtimer` (`status`; retired
  `start` answers "tracking is automatic"). State:
  `C:\ProgramData\SteamTimer\state.json`. Enforce loop: at most one kill pass
  per 60s after deadline / in cooldown (separate 60s throttle for Dota kills).
  Notes also go to the Windows Event Log (`SteamTimer` source).
- `App/` — WinForms only (`net10.0-windows`). `TrayContext`
  (ApplicationContext) is the process root: tray icon + tooltip countdown +
  **owns all balloons** via its own 15s `status` poll (daily 15m/up, session
  ending, cooldown start/over, Dota 15m/up); single `MainForm` instance
  (`ShowMain`); `MainForm` shows daily/session/cooldown/Dota,
  X hides to tray.
- `Setup/install.ps1` (Admin) — publishes, stops old service+tray and waits
  for exit, copies to `C:\Program Files\SteamTimer`, (re)creates service
  (auto + restart-on-failure), 5-min watchdog task (SYSTEM), HKCU Run entry,
  Start Menu shortcut, launches tray. `uninstall.ps1` reverses it.
- `README.md` — user-facing usage. Solution file is `SteamTimer.slnx`
  (new .slnx format — build with `dotnet build SteamTimer.slnx`).

## Policy details (v3 engine)
- Effective day budget = base(dow) + carryIn − advanceDeduction + borrowed,
  carryIn = min(30m, max(0,
  prevEffective - prevUsed)) only from the immediately previous logical day
  (multi-day gaps => 0, and forgive borrow debt). Borrow = each press -10m
  tomorrow, +(95% - 7pp per press, 0.1% floor) of 10m today (BorrowCount *
  10m becomes tomorrow's
  AdvanceDeductionSeconds); blocked on Sundays; capped so tomorrow's base never
  goes below zero; repeatable. A mid-session press extends the CURRENT session
  by today's gain (not the full 10m — that is tomorrow's loss), still capped by
  the 2h session max; no restart, no extra cooldown. Borrow() has no Running
  gate (works open/closed/cooldown); after full exhaustion a Steam relaunch is
  needed for a fresh session. Impl: `Limits` (BorrowCost=10m, 95% first, -7pp,
  0.1% floor; EffectivenessForBorrowNumber/BorrowGainForNumber/BorrowButtonText/
  FormatGain), `TimerEngine.Borrow` + `Fill` (NextBorrowEffectiveness/
  NextBorrowGainSeconds on the pipe DTO), one-click no-confirm UI in `MainForm`
  (button heats toward red as effectiveness drops) + `TrayContext` (menu text
  follows BorrowCount). Dota weekly usage carries across day rollovers inside
  the same Mon-03:00 week; week guard also resets on mismatch. Fresh day
  clears any cooldown (does not leak past 03:00).
- Cooldown = `Limits.CooldownForSession(sessionLen)` = ceil(len/5m)*5m, min 5m
  for any real session; zero-length flickers get none. While cooling, ticks
  accrue nothing and Steam launches are killed.
- Dota detection = any live `dota2` process (name match). Dota accrues the
  weekly budget only while running; Dota kill = `KillDota` (dota roots +
  children), throttled 60s, until Monday 03:00.
- `GameHistory` in state logs each seen child exe (name, full path when
  readable, total seconds, last seen; capped at 50) for future precise titles.
- Old `state.json` (v1/v2) is discarded on load (version check != 3).

## Bugs already fixed (do not regress)
1. **Start froze the UI**: `OnStart` blocked on
   `SendAsync().GetAwaiter().GetResult()` from the UI thread while awaits
   captured that context. Now: no Start path at all (auto-tracked);
   `PipeClient` still uses `ConfigureAwait(false)`; sync `Send` hops via `Task.Run`.
2. **Server killed its own connections**: `using var server` in the accept loop
   disposed pipes mid-request. Now: ownership transfers to `HandleOneAsync`.
3. **Pipe ACL**: SYSTEM service pipe unreachable from non-elevated UI. Now:
   explicit `PipeSecurity` granting `AuthenticatedUserSid` ReadWrite
   (`NamedPipeServerStreamAcl.Create`, Windows-only).
4. **install.ps1 file-locked**: service/tray hold Program Files DLLs. Now:
   stops both, waits ≤20s, force-kills, then copies.
5. **Popups only with window open**: balloons lived in `MainForm`. Now:
   `TrayContext.PollAsync` owns all notifications; X hides; Exit confirms.
6. **Overflow crash killed notifications on Sundays**: `MainForm.RefreshCountdown`
   used `Infinity` for "no manual cap" but then passed it to `Hm()`,
   throwing `OverflowException` every second AND inside the tray poll.
   Now: no Infinity flows (session remaining is always a real number);
   both `Hm()`s still return "—" for NaN/Infinity instead of throwing;
   tray poll wraps the form update in try/catch plus a catch-all so a form
   bug can never stop balloons again.

## Verified so far
- `dotnet build SteamTimer.slnx` clean (0 warn/error) — including after the v3
  rework (2h sessions, len-based cooldown, 30m carryover, Dota 9h/week,
  info-only UI).
- Parser/day matrix via throwaway console project (since deleted):
  `5h` rejected, `1h30`→1:30, 02:00 counts as previous day, reset→next 03:00.
  (Parser itself deleted with manual start; day logic unchanged.)
- Freeze repro probe (temp WinForms app calling blocking `Send` on UI thread,
  since deleted): returns in ~2s, no deadlock. Note: probe returned null
  because the *old* service binary was still installed — full live roundtrip
  against the fixed service was never run from here (shell is non-elevated).

## Current state / how to continue
- Flight Club (crash minigame + coin wallet + coin/time trades) was removed
  entirely: `App/Game/`, `App/AppSettings.cs`, `Shared/CoinExchange.cs`,
  the `Tests/` project, `FLIGHT_CLUB.md`, and all service/UI carryover for
  `CoinBudgetSeconds`/`CoinExchanges` are gone. `App` is WinForms-only again
  (no WPF). `MainForm` is DPI-scaled and vertically scrollable.
  `Program.cs` uses a session-local mutex + activation event so subsequent
  launches open the existing window.
- HEAD has the v3 automatic timer model without Flight Club. **Run
  `Setup/install.ps1` as Admin to deploy repository changes**; building and
  publishing alone do not update the installed copy. If upgrading a v1/v2
  timer, its old state is discarded by the v3 version check (fresh Dota week
  and no carryover for the first day).
- Shell here is non-elevated (medium integrity): you can build and commit,
  but cannot install/stop the service or write Program Files/ProgramData.
- Never launch Steam to test the timer without backing up
  `C:\ProgramData\SteamTimer\state.json` — with auto-tracking, just launching
  Steam burns budget and starts cooldowns.
- ## Reset policy (AGENT-ONLY — never expose to the user, README, UI, or pipe)
  Full state resets (deleting `C:\ProgramData\SteamTimer\state.json` or
  bumping `TimerState.Version` so `TimerEngine` discards old state) are
  allowed ONLY when a limit is reduced/tightened — never to forgive overuse,
  never on loosening. Tightening = daily/weekly base down, session max down,
  cooldown quantum/formula harsher, carryover down, borrow cost up /
  effectiveness or floor down / caps tighter, Dota budget down / forbidden
  days added, play windows narrowed/added. Only the agent performs the reset
  (version bump + commit); never add a reset button, never document how to
  reset in README, never add a `reset` pipe command. Non-elevated shell
  cannot wipe ProgramData directly, so reset = `Version` bump, effective on
  next `Setup/install.ps1` (Admin) restart.
  Celebration reset done: v5 -> v6 with the Mon-Fri 05:45-06:15 + 20:30-22:00
  play windows (strict tightening), full wipe on next deploy.
  Workout-gated Dota reset done: v6 -> v7, no free weekday Dota grant (strict
  tightening), full wipe on next deploy. Full history lives in `RESETS.md`.
- Open items / ideas: toast via `Microsoft.Toolkit.Uwp.Notifications` instead
  of balloon tips; IFEO/firewall hard-block as optional paranoid mode;
  resolving `GameHistory` exe names to real Steam titles (AppID lookup);
  per-game budgets beyond Dota; single-file self-contained publish.
