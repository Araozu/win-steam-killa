# WorkoutService: Notify/Tasker capture demo

An ASP.NET HTTP receiver (.NET 11) that saves raw phone uploads for analysis.
Strava and OAuth have been removed. This is **capture-only** while waiting for
real hardware samples; it does not turn uploads into Dota time.

See [WORKOUT_PIPELINE.md](../WORKOUT_PIPELINE.md) for the planned flow, runnable
PowerShell probe, Tasker setup and hardware acceptance checklist.

## Configuration

| Variable | Default | Purpose |
| --- | --- | --- |
| `API_TOKEN` | empty | Bearer token required for capture and credit endpoints; empty rejects all requests |
| `DATA_DIR` | `/data` | Captures are written to `captures/` below this directory |

## Endpoints

- `GET /health`: public liveness response including `mode: "capture-only"`.
- `POST /api/capture`: bearer-authenticated raw body, at most 256 KiB. Saves one
  JSON envelope with receipt metadata, UTF-8 text (if valid) and original bytes
  in base64. Returns `{captured,id,receivedAtUtc,creditGranted:false}` after saving.
  Retries are kept separately for inspection. Unknown body formats are accepted.
- `GET /api/credit`: bearer-authenticated HTTP 503 until real-data mapping is
  implemented. This preserves existing client cache instead of reporting zero
  earnings as a successful sync.

Old `/auth/*`, `/api/sync` and `/api/activities` routes no longer exist. Old
`strava_token.json` and `activities.json` files are ignored; existing files on a
deployed volume are not deleted automatically.

## Deploy later using the existing container setup

The Dockerfiles listen on port 8080 behind Traefik. The existing Jenkins/Ansible
setup under `+devops/` uses `workouts.araoz.dev` and mounts `./workout-data:/data`.
Copy `+devops/+develop/.env.workout.example` to
`/home/docker_admin/steamtimer-develop/.env.workout`, set `API_TOKEN`, and retain
`DATA_DIR=/data`. Ansible checks this file exists and does not rewrite it.
Captured files appear on the host at `workout-data/captures/`.

The Windows service can remain unconfigured during capture collection. Its
existing 15-minute polling is for the future credit API, not the capture endpoint.
