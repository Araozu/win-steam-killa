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
- `App/` — WinForms + embedded WPF (`net10.0-windows`). `TrayContext`
  (ApplicationContext) is the process root: tray icon + tooltip countdown +
  **owns all balloons** via its own 15s `status` poll (daily 15m/up, session
  ending, cooldown start/over, Dota 15m/up); single `MainForm` instance
  (`ShowMain`); `MainForm` shows daily/session/cooldown/Dota and Flight Club,
  X hides to tray. `App/Game/` owns the minigame's model, wallet, and graphics.
- `Tests/` — xUnit round/wallet regressions and an STA native UI/rendering test.
- `Setup/install.ps1` (Admin) — publishes, stops old service+tray and waits
  for exit, copies to `C:\Program Files\SteamTimer`, (re)creates service
  (auto + restart-on-failure), 5-min watchdog task (SYSTEM), HKCU Run entry,
  Start Menu shortcut, launches tray. `uninstall.ps1` reverses it.
- `README.md` — user-facing usage. Solution file is `SteamTimer.slnx`
  (new .slnx format — build with `dotnet build SteamTimer.slnx`).
- `FLIGHT_CLUB.md` — complete minigame usage and implementation guide.

## Policy details (v3 engine)
- Effective day budget = base(dow) + carryIn − advanceDeduction + borrowed + coinBudget,
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
- Flight Club verification (2026-10-08):
  `dotnet test SteamTimer.slnx -c Release --verbosity minimal` — 65 passed;
  `dotnet build SteamTimer.slnx -c Release` — 0 warnings/errors;
  Windows x64 app/service publishes to `publish/App` and `publish/Service`
  succeeded. Native game-state, DPI-scaled, and scrollable-host screenshots inspected. Details and screenshot
  capture commands are in `FLIGHT_CLUB.md`.
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
- **Flight Club minigame:** `App/Game/CrashGame.cs` owns monotonic round timing,
  crypto-sampled crash points, manual/auto cash-out, and the 100x flight cap.
  Its loop is autonomous: Boarding (5s) -> Flying -> Crashed (5s) -> Boarding.
  The final multiplier stays visible throughout the result display; spectator
  outcome labels are omitted during boarding, flight, and the result display.
  `Board` optionally attaches one bet without resetting takeoff; spectator
  rounds cost nothing and are recorded with Bet=0. Bets never auto-repeat.
  Actions validate their expected round ID to reject stale clicks. A long
  suspension settles the current flight once, then gives a fresh boarding
  window instead of replaying missed rounds.
  `FlightWallet.cs` atomically persists the per-user coin wallet at
  `%LOCALAPPDATA%\SteamTimer\flight-wallet.json` (not service state): 100 coins
  per 03:00 logical day, carry-over balance, no stacked offline grants, 8-round
  history. Stakes are saved before takeoff; restart recovers saved payouts and
  forfeits unpaid pending bets. Never reset a corrupt wallet silently.
  New payouts round UP to the next cent via `PayoutFor`; existing round-down
  saved results/pending payouts stay valid. The cash-out button always formats
  gains with two decimals and tabular digits.
  The wallet journals one `PendingExchange` with a stable `ExchangeWalletId`
  and sequential number; sale coins are reserved before sending. The service
  saves `CoinBudgetSeconds` and its latest `CoinExchanges` receipt per wallet
  together, before replying. Receipts survive day reset, preventing duplicate
  changes after lost replies or restarts. Rejected sales refund reserved coins.
  Sell 1 coin for +60s today; buy 10 coins for -660s of remaining time today.
  Trades do not change played time, tomorrow's debt, session/cooldown, or Dota.
  A zero effective budget must never fall back to the base for carryover.
  `FlightGameView.xaml(.cs)` is embedded beneath the existing controls through
  `ElementHost`; `FlightScene.cs` draws the animated plane/sky/trail/effects via
  WPF's Direct3D-backed compositor. MainForm activation/deactivation drives a
  2-minute idle grace period. The game then freezes its clock and bet, stops
  its model timer, and detaches composition callbacks; focusing resumes the
  same round. The service continues running. Before idle-pause, the model timer
  runs every 50ms visible / 250ms hidden, including spectator rounds;
  composition callbacks also detach immediately while hidden/disposed. WPF is
  enabled alongside WinForms in `App.csproj`. MainForm is DPI-scaled and vertically scrollable.
  `Program.cs` uses a session-local mutex + activation event so subsequent
  launches open the existing window instead of creating competing wallets.
- **Verification:** `dotnet test SteamTimer.slnx` runs the xUnit `Tests/` project,
  including isolated wallet/round and service exchange tests and STA native
  UI/rendering/idle-pause tests. Service tests use a temporary `StateStore` and
  injected clock; never construct their engine with the real state path.
  Optional `STEAMTIMER_TEST_SCREENSHOTS` saves PNG evidence. No Steam launch or
  real wallet writes occur in tests.
- HEAD has the v3 automatic timer model and Flight Club. **Run
  `Setup/install.ps1` as Admin to deploy repository changes**; building and
  publishing alone do not update the installed copy. If upgrading a v1/v2
  timer, its old state is discarded by the v3 version check (fresh Dota week
  and no carryover for the first day). The coin wallet is a separate file.
- Shell here is non-elevated (medium integrity): you can build and commit,
  but cannot install/stop the service or write Program Files/ProgramData.
- Never start a real timer in tests without backing up
  `C:\ProgramData\SteamTimer\state.json` — with auto-tracking, just launching
  Steam burns budget and starts cooldowns.
- Open items / ideas: toast via `Microsoft.Toolkit.Uwp.Notifications` instead
  of balloon tips; IFEO/firewall hard-block as optional paranoid mode;
  resolving `GameHistory` exe names to real Steam titles (AppID lookup);
  per-game budgets beyond Dota; single-file self-contained publish.
