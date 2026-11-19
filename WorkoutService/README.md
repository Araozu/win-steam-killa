# WorkoutService

An ASP.NET receiver (.NET 11) for the Simple Health → Health Connect →
Health Connect Webhook workout pipeline. It queues normalized workouts, heart
rate, and total calories for the Windows service and retains the original
uploads until cleanup.

See [WORKOUT_PIPELINE.md](../WORKOUT_PIPELINE.md) for phone setup, local testing,
and the local archive format.

## Configuration

| Variable | Default | Purpose |
| --- | --- | --- |
| `API_TOKEN` | empty | Bearer token for every `/api` endpoint. An empty token disables those endpoints. |
| `DATA_DIR` | `/data` | Uploads are written to `captures/` (queued) and `diagnostics/` (inspection only) below this directory |
| `HEALTHCHECK_URL` | `http://127.0.0.1:8080/health` | URL probed by `--healthcheck` |

## Endpoints

Every `/api` request needs `Authorization: Bearer <API_TOKEN>`. The token is
checked before the request body is read; a missing or wrong token gets 401.

- `GET /health`: public liveness response including `mode: "workout-sync"`.
- `POST /api/capture`: raw body, at most 256 KiB. Saves one JSON envelope with
  receipt metadata, UTF-8 text (if valid) and original bytes in base64.
  Returns `{captured,id,receivedAtUtc,queued}` after saving. Uploads with
  workout records are queued for the PC (`queued: true`); Test uploads, empty
  syncs and unrecognized data go to `diagnostics/` only.
- `GET /api/workouts`: fetch of up to eight pending batches.
- `POST /api/workouts/ack`: acknowledgement, with a JSON object containing an
  `ids` array of delivered batch IDs. The receiver deletes acknowledged
  captures, and expires captures and diagnostics older than one calendar
  month, on startup and every hour.

## Health check

`dotnet WorkoutService.dll --healthcheck` requests `/health` from the running
receiver and exits 0 when it answers. The container image uses it, so it needs
no curl.

## Deploy later using the existing container setup

`+devops/docker/Dockerfile` builds the image from the repository root so it can
include the shared record types. It listens on port 8080 behind Traefik and runs
as a non-root user. The Jenkins/Ansible setup under `+devops/` uses
`workouts.araoz.dev`, mounts `./workout-data:/data`, and runs the container as
the deploy user, who owns that directory (mode 0700).
Copy `+devops/+develop/.env.workout.example` to
`/home/docker_admin/steamtimer-develop/.env.workout`, set `API_TOKEN`, and retain
`DATA_DIR=/data`. Ansible checks this file exists and does not rewrite it.
Queued uploads appear on the host at `workout-data/captures/`.

The Windows service uses the same receiver credentials for the workout feed and
calculates credit locally.
