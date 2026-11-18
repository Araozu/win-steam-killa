# Code review and fixes

## Current review: WPF port (2026-11-16)

Compared the WPF app with the preceding WinForms implementation. Fixed:

- Failed tray polls now invalidate the window's cached status. A visible status
  line reports offline/stale data, the headline becomes unknown, and the window
  no longer overwrites the tray's offline warning with a cached countdown.
  Snapshot age uses a monotonic clock and estimates stop after 30 seconds.
- The headline includes the two-hour session limit while stopped and the Dota
  allowance while Dota is active. Global blocks always show zero. Estimates stop
  consuming time at the first known Steam deadline.
- Empty effective budgets display as zero, including workout-gated Dota, rather
  than substituting the eight-hour weekly cap. Startup uses unknown placeholders
  instead of mock times and bars.
- Borrow controls identify tomorrow/next week, restore their remaining balances,
  and explain the ten-minute cost and actual gain. Actions stay disabled while a
  command is pending; borrowing also requires current status and available credit.
- Action results/errors remain visible with Details collapsed. Window sizing
  respects the monitor work area in WPF units, and a minimum width keeps the
  headline readable during resizing.

Validation: Release solution build, 64 regression tests (15 new UI-state cases),
setup-script checks, and Windows x64 app publishing. WPF rendering, high DPI,
tray interaction, second-instance activation, and Windows session shutdown still
need an interactive Windows smoke test; Linux compilation cannot exercise them.

## Workout capture

`WorkoutService` captures authorized Health Connect Webhook uploads into atomic
JSON files under `DATA_DIR/captures`. Requests are limited to 256 KiB. Capture
metadata omits credential headers and query strings. `WORKOUT_PIPELINE.md`
documents the captured Simple Health records and how the PC links and acknowledges them.
The Windows service calculates credit from locally saved workouts.

Additional client/enforcement fixes from this review:

- Blank structured workout settings no longer hide environment-variable aliases.
  The host explicitly loads configuration from beside the executable.
- Workout polling runs in a separate hosted worker; sequential HTTP requests and
  timeouts cannot pause the 15-second enforcement loop.
- HTTP timeouts and malformed workout feeds preserve cached credit;
  unconfigured sources do not claim a successful sync or overwrite that cache.
  Shutdown cancellation still propagates.
- A downward workout-credit correction no longer reduces already-consumed Dota
  time. Only the weekly rollover resets that usage.

Verification: Release solution build passes with zero warnings/errors; 49 tests
pass, including raw capture formats, auth, missing tokens, concurrent uploads,
known/unknown-length size limits, unavailable credit,
configuration fallback, HTTP response validation, timeout/cache behavior,
enforcement during a pending sync and consumption after a credit correction.
Setup-script checks pass.

## Earlier timer and deployment review

Reviewed all tracked application, service, shared-policy, workout API, setup,
deployment, and demo sources. Existing policy values and state version 7 are
preserved (v7: weekday workout-gated Dota, weekends on the free grant, full
state wipe on deploy).

## Fixed defects

- Daily exhaustion now closes the session and persists its normal cooldown.
  Borrowing more time after exhaustion keeps that cooldown in effect.
- Service startup no longer bills the gap since its persisted last tick. New
  sessions do not bill the idle interval before detection, and accrual is clamped
  to the daily/session caps before computing cooldowns.
- The first tick checks/enforces immediately, including after a day rollover.
  Window transitions close sessions even when process kills are throttled.
- Remembered games and their identity history survive day rollover. State copies
  no longer share mutable collections or game records.
- Stored day/week dates use the invariant Gregorian calendar, including on
  machines using a different default calendar.
- State storage keeps an atomic backup and recovers it on primary corruption.
  Invalid primary and backup data fail startup rather than silently clear usage.
- Dota launched without a live Steam client is still detected as play and is
  included in Steam-wide kills. Remembered orphan kills require a recorded,
  matching executable path; detection remains conservative if paths are hidden.
  A process-handle leak in the auxiliary Steam detector is closed.
- The SYSTEM pipe server has explicit rights to create subsequent instances.
  Silent clients time out; input is bounded. Client cancellation covers connect,
  write, and read. Duplicate service event logging is removed.
- The UI stops extrapolating stale snapshots after 30 seconds, reports stale
  state visibly, and estimates Dota only during allowed, active play. Tray timers,
  menus, and icons are disposed. Session warnings require an active session.
- Installation checks native exit codes and stages both publishes before touching
  the installed application. It pauses the watchdog and updates the existing
  service instead of racing service deletion/recreation. Uninstallation removes
  the watchdog before stopping the service and targets the exact tray executable.
- Deployment secrets are hidden from Ansible output and excluded from Git and
  root Docker build contexts. The solution now includes WorkoutService and tests;
  the WinForms project opts into cross-platform Windows targeting.

## Verification

Verified locally on Linux: Release build succeeds with zero warnings/errors;
all 25 regression cases pass; both Windows x64 projects publish successfully;
setup-script parsing and the simulated publish-failure check pass.

Run from the repository root with .NET 11 SDK (and .NET 10 targeting/runtime
components for the Windows projects):

```sh
dotnet build SteamTimer.slnx -c Release
dotnet test Tests/Tests.csproj -c Release --no-build
pwsh -NoProfile -File Tests/VerifySetup.ps1
dotnet publish Service/Service.csproj -c Release -r win-x64 --self-contained false
dotnet publish App/App.csproj -c Release -r win-x64 --self-contained false
```

The regression project links the production engine/storage/process adapter source
so it can execute on Linux without starting a Windows service. Its fake process
adapter cannot kill real processes. Workout capture tests use an in-memory
ASP.NET server. The PowerShell check parses both setup scripts and
simulates a failed publish to verify that installation aborts.

Ansible YAML and rendered Compose templates were parsed locally. Container builds
were not run: this user cannot access the Docker daemon socket. A CI workflow
runs the build, regression suite, and setup-script check on Linux and Windows.

## Runtime validation still requiring Windows or external services

- Install/upgrade/uninstall under Administrator privileges, service recovery, and
  the SYSTEM scheduled watchdog.
- Non-elevated tray-to-SYSTEM pipe access, four concurrent clients, malformed and
  idle clients, and actual process detection/termination.
- Tray/window lifecycle, high DPI, activation from a second launch, notifications,
  and shutdown/reboot/sleep with real Steam and game processes.
- Persistent container volumes and Traefik routing need a deployed receiver.

WorkoutService provides the workout feed. The Windows service syncs it
with a bearer token and converts workout seconds to Dota credit at 3.3×,
up to the 8-hour weekly cap. Weekends use the free grant. Failed syncs keep the cached value and fresh state is fail-closed at
zero. The demo is a standalone UI mockup and does not communicate with the
service.
