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
