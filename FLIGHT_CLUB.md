# Flight Club

Flight Club is the plane-themed crash minigame below Steam Timer's time bars
and buttons. It uses local play coins and runs inside the Windows app, including
when the timer service is unreachable or Steam is blocked.
Flights loop automatically: board one during the **5-second** countdown or
watch it take off without a bet. The final multiplier lingers for **5 seconds**,
then the next boarding countdown opens automatically.

## Playing

1. Enter your bet in **BET · COINS**. The minimum is **1 coin**; the maximum is
   your current balance. Amounts accept up to two decimal places using your
   Windows region's decimal separator.
2. Optionally enable **Auto cash-out** and enter a target from **1.01×** to
   **100×**. The target is locked for that flight once you place the bet.
3. During **Boarding open**, click **Board this flight**. The stake is deducted
   immediately. Joining does not restart the countdown: the plane takes off
   when the existing **5-second** timer reaches zero, with or without you.
4. As the plane climbs, click **Cash out**, or press **Space** while the game
   has keyboard focus. Your return is the stake multiplied by the multiplier
   at the time the action is processed, rounded **up** to the next coin cent.
   The button always shows exactly **two decimal places**, with tabular digits
   to keep the amount steady as it changes.
5. If the plane flies away first, the stake is lost. If you cash out, the
   flight continues so you can see how far it would have gone.
6. After the flight ends, its final multiplier stays visible for **5 seconds**,
   then a new **5-second** boarding window opens. Each bet applies to one flight;
   choose whether to board again. Leaving a countdown alone means spectating.

Boarding closes at takeoff. You cannot join a flight already in progress,
and the betting controls lock once you board. Spectating costs no coins and
works even with an empty wallet.

The **−** button halves the entered bet, and **+** doubles it. Both clamp the
amount to the available betting range. The **10** and **25** presets also
clamp to your balance. The wallet remembers the last bet and auto-cash-out
setting used to start a flight.

The five latest crash multipliers appear above the sky. Hover over one to see
its stake and, when applicable, cash-out multiplier and total returned coins.
Flights you watched without betting are marked as such in the tooltip.
The wallet retains eight flights in total.

### Payout example

A **10-coin** bet cashed out at **2.00×** returns **20 coins**, including the
original stake. Starting from 30 coins, the balance becomes 20 when you bet
and 40 when you collect. Crashing before collection leaves it at 20.

A **1.01-coin** bet cashed out at **1.50×** returns **1.52 coins**: the exact
product is 1.515, rounded upward to the next cent. Products already at an exact
cent are unchanged.

## Weekly coins

- A new wallet starts with **30 coins**.
- Another **30 coins** is granted once per week, resetting **Monday at 03:00
  local**, on the same schedule as the Dota weekly budget.
- Unspent coins and winnings carry over. Missed weeks while the app is closed
  do not accumulate allowances; reopening grants only the current week's
  allowance if it has not already been claimed.
- Moving the clock back to an already-claimed date does not grant more coins.
- A weekly grant during a flight adds to the balance without changing the bet.
- Existing balances are preserved. An earlier daily grant counts as already
  claiming its week, so upgrading does not add a second allowance that week.
- If fewer than 1 coin remains after a flight, the game shows the next grant
  time and disables boarding until enough coins are available. Flights keep
  running so you can watch them.
- Balances are capped at **1,000,000,000,000 coins**.

Hover over the coin balance to see the weekly allowance and next reset time.

## Trading coins and today's time

The two buttons below the game connect the wallet to the SteamTimer service:

| Button | Coin change | Today's budget change |
| --- | --- | --- |
| **Sell coins** (enter a quantity) | −the chosen number of available coins | +1 minute per coin |
| **Buy 1** | +1 coin | −1 minute 6 seconds |

Each click performs one trade. Enter a positive whole number in **SELL · COINS**;
for example, selling 5 coins adds 5 minutes. Purchases are one coin at a time and
require at least 1 minute 6 seconds of today's remaining budget. Sales use
available coins, excluding coins already on board.
You can trade during a flight; trading does not alter its stake or restart it.
The coin balance and Today bar update when the service confirms the saved trade.
Purchases cannot overfill the wallet.

These are adjustments to **today's** budget, not tomorrow's borrowing or a change
to played time. Selling coins can reopen an exhausted daily budget, but the normal
2-hour session cap, cooldowns, and Dota weekly limit still apply. At 03:00 the net
trade adjustment resets; leftover time follows the existing 30-minute carryover cap.

Trades require the updated service with quantity support. If it is unavailable, new trades are disabled
while flights still work. An in-flight trade with a lost reply remains pending:
the wallet reserves sold coins, and retries the same request every 5 seconds
while the game is running. Reopening the app also recovers a pending trade.
The service saves each wallet's latest numbered receipt with the budget change,
so retries cannot trade twice. A trade not applied before the 03:00 reset is
rejected and any reserved coins are refunded. A trade already applied before
reset can still be confirmed afterward without changing the new day's budget.
Previously saved 10-coin purchases retain their original 11-minute cost and
10-coin credit when recovered; new purchases are always one coin.

## Round rules and timing

The state machine is:

```text
Boarding (5s) → Flying → Crashed (5s) → Boarding (5s) → ...
                 │
                 └─ cash-out credits coins; flight keeps going
```

Boarding optionally attaches one saved bet to the current round. Its clock
starts independently of betting. A spectator round still samples a crash point,
animates, and records its result, without deducting or crediting coins.

Each flight samples one hidden crash multiplier before takeoff. The random
source is `RandomNumberGenerator`, using 53 random bits to produce a uniform
sample `U` in `[0, 1)`:

```text
crash = clamp(floor(0.97 / (1 - U) × 100) / 100, 1, 100)
```

The live multiplier starts at 1.00× and grows exponentially:

```text
multiplier(t) = exp(0.16 × t)
```

Here `t` is seconds since boarding ended. The displayed multiplier is
rounded down to two decimal places. A 2× flight takes approximately 4.33
seconds after takeoff; 100× takes approximately 28.78 seconds.

Timing uses `Stopwatch`'s monotonic clock rather than counting rendered frames.
Every cash-out action updates the round first, so a stale display cannot
collect a bet after its actual crash time. Floating-point rounding cannot
promote a pre-crash display to the crash multiplier.
Boarding and cash-out actions also check the round ID, so a delayed click for
one flight cannot place or collect a bet on another.

**Automatic cash-out** is resolved in event-time order: if its target was
reached before the crash, it pays even when an update arrives late. A crash
at exactly the target wins the tie. The exception is the **100× flight cap**:
any remaining bet is collected automatically when the plane reaches it.

## Hiding, exiting, and saved state

Uncheck **Show Flight Club** above the game to hide it; check it again to
bring it back. The choice is saved per Windows user at
`%LOCALAPPDATA%\SteamTimer\app-settings.json` and restored on launch.
Hiding collapses the panel so the window shrinks. The wallet is untouched,
and the game model keeps ticking slowly while hidden with rendering detached,
then refreshes when shown.

Closing the main window hides it to the tray. Flights and auto-cash-out continue
during the inactivity grace period. After **2 minutes without app focus**, the
game freezes its current countdown, multiplier, result timer, and active bet.
Its update timer stops and rendering callbacks detach, including when the window
is visible behind another app. Returning to Steam Timer resumes that same round;
time spent paused does not advance the flight or settle additional rounds.
The timer service continues tracking and enforcing Steam play normally.
Rendering callbacks also detach immediately while the graphics surface is hidden.
After a long suspension, the active flight settles once and the loop opens a
fresh 5-second boarding window, rather than replaying every missed round.

The wallet is stored per Windows user at:

```text
%LOCALAPPDATA%\SteamTimer\flight-wallet.json
```

It is separate from the service's
`C:\ProgramData\SteamTimer\state.json`.

| Saved field | Purpose |
| --- | --- |
| `Version` | Wallet format version, currently 1 |
| `Balance` | Available coins, represented as a decimal |
| `LastGrantDay` | Monday of the last granted week; legacy daily dates count for their containing week |
| `LastBet` / `LastAutoCashout` | Settings used to place the last bet |
| `Pending` | Active flight ID, stake, crash point, auto target, and saved payout |
| `History` | Up to eight completed or interrupted flights, newest first; a zero stake denotes spectating |
| `ExchangeWalletId` / `ExchangeNumber` | Persistent wallet identity and last completed trade number |
| `PendingExchange` | Journaled coin/time trade awaiting a matching service receipt |

The service's `state.json` saves `CoinBudgetSeconds` (today's net trade adjustment)
and `CoinExchanges` (the latest durable receipt per wallet). Receipt history
survives daily rollovers even though the adjustment itself resets.

Each mutation writes a temporary file, flushes it to disk, then replaces the
wallet file. The in-memory wallet changes only after the replacement succeeds.
The stake is saved **when you board**; a payout is saved **before** it is
shown as collected.

If the application exits during a flight, the next launch records that flight
in history and opens a new boarding window. A previously saved payout remains
credited. An unpaid stake stays lost; restarting does not refund it or resume
the flight. Recovery is recorded once, so reopening again cannot pay twice.
Existing history and pending payouts from the earlier round-down calculation
remain valid and keep their saved values. Newly collected payouts round up.

Unreadable or invalid wallet data is preserved. The game displays a wallet
error and disables betting instead of silently replacing the saved balance.

## Graphics and application architecture

The existing app is WinForms. A WPF `UserControl` is embedded through
`System.Windows.Forms.Integration.ElementHost`, below the timer controls.
WPF uses Windows' Direct3D-backed compositor with automatic software fallback
when hardware acceleration is unavailable. No OpenGL/Vulkan package, external
native graphics DLL, image asset download, or browser is required.

The graphics are vector paths and gradients: a red propeller plane, animated
stars and clouds, a moving runway, a curved glowing trail, departure particles,
and cash-out celebration particles. `CompositionTarget.Rendering` schedules
visual updates, limited to at most 60 per second. The independent game timer
updates the loop every 50 ms while visible and every 250 ms while hidden during
the grace period, then stops entirely when idle-paused. Drawing does not settle
bets or mutate the wallet; the graphics animation clock freezes too.

The host window supports DPI scaling and vertical scrolling on shorter
desktops. Relaunching the app signals the existing instance to open its window
through a session-local mutex and activation event.

| File | Responsibility |
| --- | --- |
| [CrashGame.cs](App/Game/CrashGame.cs) | Round state, randomness, monotonic timing, cash-out ordering |
| [FlightWallet.cs](App/Game/FlightWallet.cs) | Weekly grants, decimal accounting, atomic persistence, recovery |
| [FlightGameView.xaml](App/Game/FlightGameView.xaml) | Game layout, controls, and visual styles |
| [FlightGameView.xaml.cs](App/Game/FlightGameView.xaml.cs) | Input validation, controls, HUD, history, and model timer |
| [FlightScene.cs](App/Game/FlightScene.cs) | Animated vector graphics and composition lifecycle |
| [MainForm.cs](App/MainForm.cs) | WinForms host, scrolling, DPI scaling, Flight Club show/hide toggle, and disposal |
| [AppSettings.cs](App/AppSettings.cs) | Per-user UI preferences (Flight Club visibility) with atomic persistence |
| [Program.cs](App/Program.cs) | Single-instance startup and window activation |
| [CrashGameTests.cs](Tests/CrashGameTests.cs) | Wallet and round regression tests |
| [FlightViewTests.cs](Tests/FlightViewTests.cs) | STA native rendering and interaction test |
| [CoinExchangeTests.cs](Tests/CoinExchangeTests.cs) | Coin/time accounting, durable retries, daily resets, and failed-save recovery |

## Building, checking, and installing

Use Windows with the **.NET 10 SDK**. The installed app uses the **.NET 10
Windows Desktop Runtime**, which includes WinForms and WPF.

From the repository root:

```powershell
dotnet build SteamTimer.slnx -c Release
dotnet test SteamTimer.slnx -c Release --verbosity minimal
dotnet publish App/App.csproj -c Release -r win-x64 --self-contained false -o publish/App
dotnet publish Service/Service.csproj -c Release -r win-x64 --self-contained false -o publish/Service
```

The test suite uses temporary wallets and deterministic clocks. The native UI
test runs on an STA thread and checks input validation, takeoff, automatic and
keyboard cash-out, spectator flights, hidden-flight completion, the 5-second
result display and boarding countdown, two-decimal payout display, DPI-scaled
rendering, and the scrollable WinForms host. Tests do not launch Steam or write
to the real coin wallet or service state. Exchange tests use temporary service
stores and cover both rates, retries/restarts, insufficient time, reset races,
and failed saves. Idle tests check visible and hidden panels stop model ticks
and rendering at the 2-minute boundary and resume their preserved round.

To save screenshots of the native game states and host window:

```powershell
$env:STEAMTIMER_TEST_SCREENSHOTS = "$env:TEMP\SteamTimer-flight-evidence"
dotnet test SteamTimer.slnx -c Release --verbosity minimal
```

Verification on **2026-10-08**, before the weekly allowance and trade-quantity changes:
**65 tests passed**, Release solution build
completed with **0 warnings and 0 errors**, and the Windows x64 app published
successfully alongside the updated service. Screenshots of flight states, scaled
graphics, and the actual host window were also inspected.

For the subsequent weekly allowance and trade-quantity changes, test expectations
were updated but **tests were not run**, at the user's request. The Release
solution build completed with **0 warnings and 0 errors**, and both the app and
service were published successfully.

To update the installed copy, run [Setup/install.ps1](Setup/install.ps1) as
Administrator and reopen Steam Timer. Building or publishing alone updates
repository output files, not the installed files under Program Files.
