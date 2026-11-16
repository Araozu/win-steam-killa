# Workout pipeline: capture first, credit later

Status: **waiting for hardware and real Notify/Tasker samples**. Strava has been
removed. The HTTP receiver is a toy data-capture service; uploads earn no Dota
time. Do not implement a workout parser or turn capture receipts into credit
until the checklist below is complete.

## Intended flow

```text
Xiaomi wearable
    -> Notify for Xiaomi on Android
    -> Tasker event + whatever workout fields actually exist
    -> HTTPS POST /api/capture on the VPS
    -> /data/captures/<server-generated-id>.json
    -> inspect samples and agree on a normalized workout schema
    -> [future] validated, deduplicated workout records
    -> [future] daily credit API
    -> Windows SteamTimer -> weekday Dota allowance
```

The VPS receives phone uploads while the PC is off. The existing Docker/Traefik
deployment can host the receiver. A local PC server is also useful for initial
capture tests, but `localhost` on the phone refers to the phone, not the PC.

## What is known, and what is still unknown

Notify documents Tasker integration under Smart assistant and workout start/end
custom triggers under Features -> Custom triggers, with Tasker as their action:
`com.mc.xiaomi.tasker.trigger.workoutStarted` and
`com.mc.xiaomi.tasker.trigger.workoutFinished`. The documentation does **not**
specify workout duration, start time, or ID extras for these triggers. Event
delivery and available fields must be tested on the actual wearable/app version.
[Notify's Xiaomi Tasker integration](https://www.mibandnotify.com/help/tasker_xiaomi_help.php)

A trigger might contain only an event name. If so, merely forwarding it does not
provide workout duration. Compare any exposed extras, Notify's workout summary,
and available exports before deciding whether a usable data source exists. Do
not equate time between phone notifications with active workout time: pauses,
late sync, offline delivery, and phone restarts can make that wrong.

Tasker's HTTP Request action supports a URL, headers, a raw text body, and file
uploads. This is the transport to the receiver, independent of the eventual
workout schema. [Tasker HTTP Request documentation](https://tasker.joaoapps.com/userguide/en/help/ah_http_request.html)

## Tasker feasibility and export investigation (2026-11-16)

**Decision:** use Tasker as the phone-side automation and transport. First test
whether Notify can supply completed workout records; do not infer workouts from
steps or heart-rate samples. The export command is worth testing, but is not yet
a confirmed workout source.

### Documented Notify commands

The [Xiaomi integration reference](https://www.mibandnotify.com/help/tasker_xiaomi_help.php?lang=en)
lists `com.mc.xiaomi.taskerExportAllSpreadsheetData` for steps, sleep, heart and
weight. It does **not** promise workout rows. Optional `start` and `end` extras
are long Unix timestamps in milliseconds; defaults cover local midnight to now.
Exports go under `export` in the configured backup folder, otherwise the app's
external files directory. No completion broadcast or filename contract is given.

Use Tasker **Send Intent**, target **Broadcast Receiver**, action above, package
`com.mc.xiaomi1`; leave Class, Data, MIME and categories empty. The
[Notify send tutorial](https://www.mibandnotify.com/help/tasker_send_intent_tutorial.php)
confirms the package. Include `password:YOUR_NOTIFY_PASSWORD` if configured;
this is separate from the receiver's bearer token. For a reproducible probe, use
literal millisecond bounds and force long typing, e.g. `start:1735689600000L`
and `end:1735776000000L` (example dates only; replace with the workout window).
[Tasker Send Intent typing](https://tasker.joaoapps.com/userguide/en/intents.html)

### Probe order and evidence

1. **Transport first:** run the synthetic HTTP probe below, then send a literal
   Tasker body. Check the saved envelope before introducing wearable data.
2. **Folder access:** select a backup destination in Notify and verify Tasker can
   list and read its exports on this Android version. Do not assume Tasker can
   access another app's `Android/data` directory. If access fails, try a mutually
   accessible destination using the apps' supported folder selection; otherwise
   use manual sharing for sample collection and mark automation blocked.
3. **One manual export:** export a narrow interval containing one known workout.
   Record actual filenames, format, sizes, sheets/columns and units, and compare
   them with the workout summary. Check explicitly for completed workout rows,
   active duration, timestamps, pauses, activity type and stable identity.
4. **One Tasker export:** run Send Intent with the same explicit interval and
   compare the result with the manual export. A successful Send Intent action
   alone does not prove Notify handled it or finished writing files.
5. **Completion experiment:** record file changes and export latency. Since no
   callback is documented, prototype bounded polling with a deadline, checking
   for changed files and stable size/mtime across multiple observations. Copy to
   an immutable local queue and validate file integrity before upload; stability
   alone cannot guarantee completion. Serialize exports to avoid overwrites.
6. **Trigger experiment:** capture workoutStarted/workoutFinished independently,
   logging `%evtprm1` and only observed extras. Tasker exposes broadcast extras as
   local variables with normalized names; it cannot receive every intent type.
   [Tasker receiving rules](https://tasker.joaoapps.com/userguide/en/intents.html)
   Test whether finish arrives before workout data is synced; retry export after
   sync if needed. Do not assume `syncData` returns a completion acknowledgement.
7. **Recovery:** rerun an overlapping export window after offline sync and phone
   restart. Use explicit bounds spanning the previous logical day as well as
   today; the export default would miss yesterday's late workout after midnight.
   Record repeated rows and edits so later normalization can deduplicate them.

### Upload files without changing their contents

Use HTTP Request `POST /api/capture`, the Authorization header below, **File To
Send** set to the discovered local queue file, and Body empty. For a binary
probe use `Content-Type:application/octet-stream`. Do not prefix the path with a
multipart field name: the capture endpoint stores request bytes and does not
extract multipart attachments. Send case notes as a separate text capture and
keep a local mapping from queued filename to returned capture ID.
[Tasker file upload behavior](https://tasker.joaoapps.com/userguide/en/help/ah_http_request.html)

Inspect `%http_response_code` and `%http_data`, handle action errors, and remove
an item from the pending queue only after a successful capture response. Use
bounded retry delays for network/5xx failures; keep 401/413 items for correction.
The receiver's 256 KiB limit includes the entire request. Measure export size
before sending; narrow the interval or preserve the file locally if too large.
Do not silently truncate or split a spreadsheet archive into invalid files.
Keep original binary files; Tasker's Read File is for text, not XLSX/ZIP.
[Tasker Read File](https://tasker.joaoapps.com/userguide/en/help/ah_read_file.html)

### Source selection gate

| Observed result | Next step |
| --- | --- |
| Triggers expose reliable completed workout fields | Prefer direct event capture; use exports to reconcile missed events if suitable. |
| Exports contain usable completed workout records | Prototype export ingestion on the server using real fixtures; finish events can request an export, with periodic catch-up. |
| Exports contain only daily totals or sensor samples | Keep them diagnostic; investigate Notify's workout-specific manual export/share before choosing another supported source. |
| Export files cannot be read automatically | Collect manually shared samples; resolve access before promising unattended operation. |

Deliverables before implementing credit: one original export with case notes,
a field/units/identity mapping, a repeat-sync sample, and a demonstrated recovery
from offline delivery. After the probe succeeds, create an importable Tasker
project with setup variables, serialized export handling, a durable upload queue
and visible failure status. Avoid publishing guessed Tasker XML or paths before
these checks. The server remains capture-only until the hardware checklist and
normalization rules are satisfied.

## Run the toy receiver now

From the repository root in PowerShell, with the .NET 11 SDK:

```powershell
$env:API_TOKEN = 'replace-with-a-long-random-token'
$env:DATA_DIR = Join-Path $PWD 'artifacts/workout-capture'
dotnet run --project WorkoutService/WorkoutService.csproj --no-launch-profile -- --urls http://0.0.0.0:5080
```

This command runs in the foreground. Use a second terminal for a synthetic probe
(set the same token in that terminal):

```powershell
$captureToken = 'replace-with-a-long-random-token'
Invoke-RestMethod -Method Post -Uri 'http://localhost:5080/api/capture' `
  -Headers @{ Authorization = "Bearer $captureToken" } `
  -ContentType 'text/plain; charset=utf-8' `
  -Body "SYNTHETIC TEST ONLY`nevent=workoutFinished`nraw sample goes here"

Get-ChildItem ./artifacts/workout-capture/captures/*.json |
  ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }
```

For a phone on the same Wi-Fi use `http://<PC-LAN-IP>:5080/api/capture`; allow the
port on the private network if necessary. Use the existing HTTPS Traefik host
for the VPS (`https://workouts.araoz.dev/api/capture`). Send the bearer token in
the Authorization header. The receiver needs no Strava credentials or OAuth.

## Configure Tasker when hardware is ready

1. Enable Notify's Tasker integration and configure the workout start/end
   triggers. Record the Notify, Tasker, Android and wearable versions.
2. Create corresponding Tasker Intent Received profiles using the documented
   action names above. Confirm both profiles actually fire before adding HTTP.
3. Inspect the event's exposed variables/extras on the phone. Save their exact
   names, values, missing/unexpanded values, and a matching Notify workout summary.
   Do not invent `%duration`, `%start_time`, or a workout ID variable.
4. Add Tasker **HTTP Request**: method `POST`, URL the receiver's `/api/capture`,
   timeout 30 seconds, with these headers (one per line):

   ```text
   Authorization:Bearer YOUR_API_TOKEN
   Content-Type:text/plain; charset=utf-8
   ```

5. For the first probe use a literal body such as `probe=workoutFinished`. Once
   that arrives, add labeled copies of the **actual** variables discovered in
   step 3. Plain text avoids guessing a JSON schema and escaping variable values.
   If Notify supplies an export instead, send the file and record how it was
   obtained. The server preserves exactly the bytes Tasker sends; it cannot see
   Android extras Tasker did not forward.
6. Save a local copy before uploading. Record the response and retry a failed
   upload later with the same body. Captures intentionally retain retries as
   separate files; deduplication belongs to the later credit implementation.

## What is saved

Each successful POST creates one JSON file in `DATA_DIR/captures`. Its fields:

| Field | Meaning |
| --- | --- |
| `Id` | Server-generated ID, also the filename |
| `ReceivedAtUtc` | Server receipt timestamp; **not** workout start/end |
| `ContentType`, `UserAgent` | Selected request metadata |
| `BodyLength` | Raw byte count |
| `BodyText` | Exact UTF-8 text, or null for invalid UTF-8 |
| `BodyBase64` | Original bytes, including binary data |

JSON, forms, plain text, malformed JSON, and binary bodies are accepted without
interpretation. Authorization, cookies and query strings are not saved. Secrets
placed inside the body will be saved, so keep tokens in the header. Bodies are
limited to 256 KiB; oversized requests return 413 and save nothing. Missing or
wrong tokens return 401. There is no HTTP endpoint to browse captured samples.
Files persist across restarts; there is no automatic cleanup or retention policy
in this toy server. Review disk use during collection and keep useful samples.

## Hardware acceptance checklist

- [ ] Confirm supported start/end triggers and capture all available fields.
- [ ] Compare manual and Tasker spreadsheet exports; establish whether workout
      records actually exist, rather than only sensor samples/daily aggregates.
- [ ] Verify Tasker file access, export completion handling, upload sizes and
      queued retry after phone restart.
- [ ] Capture one normal workout and compare it with the Notify summary.
- [ ] Capture pause/resume, a short workout, and a cancelled workout.
- [ ] Capture repeat sync / repeated finish notifications for the same workout.
- [ ] Complete a workout offline, reconnect, and check late delivery.
- [ ] Restart the phone/Notify/Tasker and check whether old workouts reappear.
- [ ] Check duration units, active versus elapsed duration, and timestamp units
      (seconds versus milliseconds), timezone, and stable workout identity.
- [ ] Collect a workout near midnight / the 03:00 logical-day boundary.
- [ ] Confirm raw files survive receiver/container restart on the mounted volume.

Keep a short case note beside each saved sample: test scenario, observed local
start/end, pause time, Notify summary and versions. Label synthetic probes so
they cannot later be confused with wearable data.

## Decisions after samples exist

Choose a stable identity that survives retry/re-sync, reliable completed-workout
duration, timestamps with explicit timezone, and rules for cancelled/edited data.
If the triggers lack enough data, investigate a supported workout export or
another source before enabling credit. A start/end event pair alone is not an
approved substitute.

Then implement normalization, validation, atomic upserts, duplicate protection,
phone retry/queue behavior and tests based on the captured cases. Restore
`GET /api/credit?day=yyyy-MM-dd` only after that work is verified end to end.
The previous defaults (4x conversion, 10-minute minimum, 4h daily credit cap,
8h weekly timer cap, 03:00 logical-day boundary) are reference values to revisit
then, not rules implemented by this capture server.

For now `/api/credit` returns authenticated HTTP 503. The Windows client preserves
any existing weekly cache on that response; fresh state has zero weekday credit,
and weekly rollover still clears it. Captures cannot unlock Dota. Existing
weekend grants, forbidden days, borrowing, daily budgets and play windows remain
as implemented by the timer. No production mapping or hardware validation is
claimed complete.
