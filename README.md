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
  balloons only for warnings/kill/cooldown/Dota), plus the Flight Club minigame.
- `Shared/` — `Limits` (2h session max, len-based cooldown, 30m carryover,
  9h Dota week), `LogicalDay` (03:00 reset), pipe DTOs.

## Flight Club

An Aviator-style crash game sits below the timer controls. The red
propeller plane takes off, a multiplier climbs, and you try to cash out before
it flies away. The animated sky, plane, glowing trail, and effects use WPF's
Direct3D-backed graphics inside the WinForms window; no extra graphics runtime
or asset downloads are needed.

- Start with **100 coins**. Another **100 coins** arrives once per logical day
  at **03:00 local**. Unspent coins carry over; missed offline days don't stack.
- Flights run **automatically**. The final multiplier lingers for **5 seconds**,
  followed by a **5-second boarding countdown**. Enter a bet (minimum **1 coin**,
  up to two decimals), then **Board this flight** before the countdown ends.
  During flight, click **Cash out** or press **Space** while the game has focus.
- Leave boarding alone to watch a flight without betting. Each bet applies to
  just one flight; watching costs no coins, even with an empty wallet.
- Optional **auto cash-out** collects at a chosen multiplier, from **1.01×**
  to **100×**. A crash at exactly your target happens first. At the **100×**
  flight cap, any remaining bet collects automatically.
- Cash-out returns **bet × multiplier**, rounded **up** to the next coin cent.
  The button shows exactly **two decimal places** with tabular digits.
  The flight continues after you cash out so you can see its final multiplier.
  Recent flights have tooltips showing your bet and payout.
- **Sell 1 coin → add 1 minute to today's budget.** **Buy 10 coins → spend
  11 minutes of today's remaining budget.** The buttons below the game update
  the coin balance and Today bar immediately. Trades require the updated Windows
  service; held bets cannot be sold, and you cannot spend time already played.
  Saved transactions recover after a timeout or restart without double charges.
- After **2 minutes without app focus**, the game pauses its clock, stops its
  update timer, and detaches rendering. Returning resumes the same countdown or
  flight. This also applies when hidden in the tray; the timer service keeps running.
- Exiting/restarting retains any saved payout; an unsettled bet is lost.
  Coins and history are saved in
  `%LOCALAPPDATA%\SteamTimer\flight-wallet.json`, separately from timer state.
- Outcomes use cryptographic randomness with a 97% survival curve
  (`floor(0.97 / (1 - U) × 100) / 100`, clamped to 1–100×).

The window scrolls on smaller desktops. Flights work without the Windows service;
coin/time trades need it. Trades change today's Steam budget; the 2-hour session
cap, normal cooldowns, and Dota's weekly budget still apply.

See the [Flight Club guide](FLIGHT_CLUB.md) for controls, timing rules,
wallet recovery, graphics architecture, and build/test instructions.

## Dev

```powershell
dotnet build SteamTimer.slnx
dotnet test SteamTimer.slnx
```

Requires .NET 10 SDK + Windows. Service must run on Windows
(EventLog, Toolhelp32, named pipes).
Tests use temporary wallets and deterministic clocks; the UI test renders the
native game and checks betting, cash-out, hiding, DPI-scaled graphics, and the
scrollable WinForms host without launching Steam. Set
`STEAMTIMER_TEST_SCREENSHOTS` to an output directory to save visual evidence.

## Notes / limits

- Self-control grade: you are Admin, so you *could* `sc stop SteamTimer` or
  delete state. It just makes it annoying by accident, which is the point.
- Steam + child processes are killed (client, helpers, overlay, launched games).
  Dota lockouts kill only `dota2` processes.
- The service logs observed Steam child games (exe name + path + time) in
  state for future precise per-game tracking.
- To fully reset: `Setup/uninstall.ps1` as admin, then delete
  `C:\ProgramData\SteamTimer`.
