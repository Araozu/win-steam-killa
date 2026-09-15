# SteamTimer — agent handover

## What this is
Self-control Steam limiter for Windows. One timer per day, max **3h**
(hard-coded, non-overridable), day resets at **03:00 local**. Warns at T-15m,
kills Steam + launched games at deadline, keeps enforcing until 03:00.
Survives reboot. Self-control grade (user keeps Admin): friction, not fortress.

## Layout (`C:\Users\Fernando\proj\steam-timer\`, git repo, `main`, no remote)
- `Shared/` — `Limits.cs` (3h cap, 1m min, 15m warn, 03:00 reset, pipe name,
  state path), `DurationParser.cs` (`1h`, `1h30m`, `90m`, `1:30`, bare = minutes),
  `LogicalDay.cs`, `TimerState.cs` + pipe DTOs.
- `Service/` — .NET 10 Worker, runs as **LocalSystem** (`AddWindowsService`,
  name `SteamTimer`). `TimerEngine` owns state, `Worker` ticks every 15s,
  `Killer` kills by name + descendant-of-Steam tree (Toolhelp32 parent PIDs,
  no NuGet), `PipeServer` = JSON-lines over named pipe `steamtimer`
  (`start`/`status`). State: `C:\ProgramData\SteamTimer\state.json`.
  Enforce loop: at most one kill pass per 60s after deadline, until 03:00 reset.
  Notes also go to the Windows Event Log (`SteamTimer` source).
- `App/` — WinForms (`net10.0-windows`). `TrayContext` (ApplicationContext) is
  the process root: tray icon + tooltip countdown + **owns all balloons** via
  its own 15s `status` poll; single `MainForm` instance (`ShowMain`);
  `MainForm` is display-only (presets, start, countdown), X hides to tray.
- `Setup/install.ps1` (Admin) — publishes, stops old service+tray and waits
  for exit, copies to `C:\Program Files\SteamTimer`, (re)creates service
  (auto + restart-on-failure), 5-min watchdog task (SYSTEM), HKCU Run entry,
  Start Menu shortcut, launches tray. `uninstall.ps1` reverses it.
- `README.md` — user-facing usage. Solution file is `SteamTimer.slnx`
  (new .slnx format — build with `dotnet build SteamTimer.slnx`).

## Bugs already fixed (do not regress)
1. **Start froze the UI**: `OnStart` blocked on
   `SendAsync().GetAwaiter().GetResult()` from the UI thread while awaits
   captured that context. Now: `async void OnStart` + `await SendAsync`;
   `PipeClient` uses `ConfigureAwait(false)`; sync `Send` hops via `Task.Run`.
2. **Server killed its own connections**: `using var server` in the accept loop
   disposed pipes mid-request. Now: ownership transfers to `HandleOneAsync`.
3. **Pipe ACL**: SYSTEM service pipe unreachable from non-elevated UI. Now:
   explicit `PipeSecurity` granting `AuthenticatedUserSid` ReadWrite
   (`NamedPipeServerStreamAcl.Create`, Windows-only).
4. **install.ps1 file-locked**: service/tray hold Program Files DLLs. Now:
   stops both, waits ≤20s, force-kills, then copies.
5. **Popups only with window open**: balloons lived in `MainForm`. Now:
   `TrayContext.PollAsync` owns all notifications; X hides; Exit confirms.

## Verified so far
- `dotnet build SteamTimer.slnx` clean (0 warn/error).
- Parser/day matrix via throwaway console project (since deleted):
  `5h` rejected, `1h30`→1:30, 02:00 counts as previous day, reset→next 03:00.
- Freeze repro probe (temp WinForms app calling blocking `Send` on UI thread,
  since deleted): returns in ~2s, no deadlock. Note: probe returned null
  because the *old* service binary was still installed — full live roundtrip
  against the fixed service was never run from here (shell is non-elevated).

## Current state / how to continue
- HEAD has the tray-notifier + installer fixes. **User must re-run
  `Setup/install.ps1` as Admin to deploy them** (last deploy may predate fixes).
- Shell here is non-elevated (medium integrity): you can build and commit,
  but cannot install/stop the service or write Program Files/ProgramData.
- Never start a real timer in tests without backing up
  `C:\ProgramData\SteamTimer\state.json` — a `start` burns the day's one timer.
- Open items / ideas: toast via `Microsoft.Toolkit.Uwp.Notifications` instead
  of balloon tips; IFEO/firewall hard-block as optional paranoid mode;
  `5h rejected`-style UX polish; single-file self-contained publish.
