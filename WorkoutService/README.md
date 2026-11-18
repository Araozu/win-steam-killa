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
| `API_TOKEN` | empty | Bearer token for workout endpoints. An empty token disables those endpoints. |
| `DATA_DIR` | `/data` | Captures are written to `captures/` below this directory |

## Endpoints

- `GET /health`: public liveness response including `mode: "workout-sync"`.
- `POST /api/capture`: bearer-authenticated raw body, at most 256 KiB. Saves one
  JSON envelope with receipt metadata, UTF-8 text (if valid) and original bytes
  in base64. Returns `{captured,id,receivedAtUtc,creditGranted:false}` after saving.
  Test uploads and unrecognized data are retained only for diagnostics.
- `GET /api/workouts`: bearer-authenticated fetch of up to eight pending batches.
- `POST /api/workouts/ack`: bearer-authenticated acknowledgement, with a JSON
  object containing an `ids` array of delivered batch IDs. The receiver clears
  acknowledged captures and captures older than one calendar month on startup
  and every hour.
- `GET /api/credit`: compatibility response for earlier clients. Returns HTTP
  503 so those clients retain cached credit.

## Deploy later using the existing container setup

The Dockerfiles listen on port 8080 behind Traefik. The existing Jenkins/Ansible
setup under `+devops/` uses `workouts.araoz.dev` and mounts `./workout-data:/data`.
Copy `+devops/+develop/.env.workout.example` to
`/home/docker_admin/steamtimer-develop/.env.workout`, set `API_TOKEN`, and retain
`DATA_DIR=/data`. Ansible checks this file exists and does not rewrite it.
Captured files appear on the host at `workout-data/captures/`.

Build the container from the repository root so Docker can include the shared
record types. The Windows service uses the same receiver credentials for the
workout feed and calculates credit locally.
