# Simple Health workout pipeline

WorkoutService captures and normalizes Health Connect uploads. The SteamTimer
Windows service saves them locally, links related records, and calculates
Dota 2 credit from the recorded workout durations.

Watch → Notify → Simple Health → Health Connect → Health Connect Webhook →
WorkoutService → SteamTimer's local workout archive.

## Observed Health Connect records

HC Webhook 1.9.32 sent a Test payload and then an exercise-only sync over
Tailscale. One sync contained an exercise session from Simple Health
(`com.mc.simplehealth`). A later upload contained 11 overlapping one-minute
heart-rate buckets and a calorie record with exactly the same start and end.
The original captures are local and Git-ignored.

| Payload field | Mapping and observed result |
| --- | --- |
| Top-level `test` | `true` on the Test payload. Keep test batches out of the PC archive. |
| `metadata.data_origin`, `metadata.id` | Source app and Health Connect record ID. Accept Simple Health records and upsert workouts by Health Connect ID. |
| `metadata.last_modified_time` | Source revision timestamp. Use it to order updates to the same record. |
| `start_time`, `end_time` | Parse as offset-bearing instants. The sample spans 612.184 seconds. Keep the timestamps with their offsets. |
| `duration_seconds` | 612 seconds. HC Webhook calculates elapsed time from the endpoints. The payload carries no paused or active duration. |
| `metadata.recording_method` | `manual_entry` on the session started in Notify and `actively_recorded` on its heart-rate data. Preserve both labels as supplied. |
| `type`, `title` | Generic exercise type `"0"` and a display title. Keep these as descriptive fields. |

## Local sync and retention

In Steam Timer, open **Workout sync**, enter the HTTPS receiver URL and its
sync bearer token, and click **Save settings**. Click **Sync now** to fetch
pending workouts immediately and check the last successful sync. The service
saves these settings in its own profile and polls automatically about every
15 minutes. To rotate a token, enter the replacement and save again.

For a service configured outside the app, `Workout:BaseUrl` and
`Workout:ApiToken` or their `WORKOUT_BASE_URL` and `WORKOUT_API_TOKEN`
environment aliases provide defaults until settings are saved in the app.
With the default LocalSystem install, the archive is
`%SystemRoot%\System32\config\systemprofile\AppData\Local\SteamTimer\workouts.json`.

The service fetches pending batches from `GET /api/workouts`, merges them into
the local archive, saves it atomically, then acknowledges those batch IDs at
`POST /api/workouts/ack`. A failed local save leaves the batch pending. An
acknowledgement retry is safe. This queue has one consuming PC.

Workout edits use the original record ID and last modification time. The archive
retains deletion markers so an older upload cannot bring a deleted record back.
Heart-rate samples link by source and time. Set the phone's heart-rate resolution
to **1 minute** for aggregate buckets, or **Full** for individual readings.
Partial overlap at the edges is enough to link an aggregate. The original values
are preserved. Total calorie records link by source and exact start and end.
Links can be made after the exercise and sensor uploads arrive on different polls.
The archive's `sensorLinks` maps each workout ID to keys in `heartRate`
and `totalCalories`. It holds records and their links for use by the desktop.

The PC keeps 90 days of records. Each sync drops older sessions, sensor
readings and deletion markers. Every save keeps the previous archive as
`workouts.json.bak`. If the archive is unreadable, the service loads the
backup. If both are unreadable, it renames them to `*.corrupt-<time>` and starts
an empty archive, so one damaged file cannot stop every future sync.

The receiver moves acknowledged batches out of the pending queue. Cleanup runs
on startup and hourly. It deletes acknowledged captures and expires any
remaining captures and diagnostics older than one calendar month.

## Dota 2 credit on the PC

SteamTimer counts deduplicated workouts that started in the current UTC-05
week, from Monday at 00:00 through the current day. Workouts must be synced
before the next Monday reset to earn credit. The start day determines the
budget, so a Friday workout synced on Saturday adds to the weekend through
weekday carryover. A Sunday workout first synced on Monday stays in the
archive and earns no credit.

Each budget receives 3.3 times its eligible workout seconds, rounded down to
a whole second, with no credit cap. Overlapping workouts count their shared
time once. The 612-second sample earns 2,019 seconds (33 min 39 sec). The weekend
budget includes a 4-hour base plus unused weekday credit and weekend workouts.

## Capture a workout from Android

1. Sync the workout from the watch into Notify. Open its workout details and tap
   **Sync** to send it through Simple Health. Verify the entry under **Health
   Connect → Data and access → Activity → Exercise → See all entries**.
   [Notify workout sync](https://www.mibandnotify.com/help/healthconnect_workouts_sync.php)
2. Install **Health Connect Webhook**. Under **What to sync**, enable **Exercise**
   and grant its Health Connect read permission. Enable **Heart rate** and
   **Total calories** if you want those records linked.
   [Android app releases](https://github.com/mcnaveen/health-connect-webhook/releases/latest)
3. Add a JSON webhook pointing to the receiver's `/api/capture` URL. Set the
   header name to `Authorization` and the value to `Bearer YOUR_CAPTURE_API_TOKEN`.
   Use the receiver's Tailscale address if both devices are on the same tailnet,
   or the HTTPS deployment URL. [Webhook configuration](https://github.com/mcnaveen/health-connect-webhook/blob/main/docs/webhook.md)
4. Tap **Sync now** and check for HTTP 200 in **Webhook log**. Inspect the saved
   envelope's `BodyText` for a populated `exercise` array. Choose a manual date
   range if the workout predates the default sync window.
   [Sync and delivery logs](https://github.com/mcnaveen/health-connect-webhook/blob/main/docs/usage.md)

## Run the capture receiver

From the repository root with the .NET 11 SDK, set the shared token and
a private data directory. For a Linux host on the tailnet:

```sh
umask 077
export API_TOKEN="$(openssl rand -hex 24)"
export DATA_DIR="$PWD/artifacts/workout-capture"
dotnet run --project WorkoutService/WorkoutService.csproj --no-launch-profile -- --urls "http://$(tailscale ip -4):5080"
```

Use `http://<receiver-tailscale-ip>:5080/api/capture` on the phone. For the
persistent deployment, follow the [WorkoutService setup](WorkoutService/README.md).

Each accepted POST saves one JSON envelope with receipt metadata and the
original body. Uploads with workout records go to `DATA_DIR/captures/<id>.json`
and wait there for the PC. Everything else (Test payloads, empty syncs,
unknown formats) goes to `DATA_DIR/diagnostics/<id>.json` and is never queued.
The service limits uploads to 256 KiB and keeps each retry as a separate
capture. Records from apps other than Simple Health, and rows that fail
validation, are left out and counted in the receiver's log. Inspect the inner
JSON locally:

```sh
jq '.BodyText | fromjson' "$DATA_DIR/captures/<id>.json"
```

The capture endpoint returns a receipt with `queued: true` when the upload
contained workout records. SteamTimer fetches the records and calculates Dota
credit locally.
