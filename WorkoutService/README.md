# WorkoutService (Option A: Mi Fitness -> Strava -> VPS -> SteamTimer)

Thin ASP.NET Minimal API (.NET 11) that caches Strava activities and serves daily workout credit.
SteamTimer will poll `GET /api/credit?day=yyyy-MM-dd` with a bearer token.

Rule served: `dotaSeconds = workoutSeconds * WORKOUT_MULTIPLIER` (default 4x),
`day` = logical day (03:00 reset in `USER_TIMEZONE`, must match the PC timezone).

## Strava app setup (once)

1. Create a Strava API app, set Authorization Callback Domain to your host
   (e.g. `workouts.example.com`), note client ID/secret.
2. Deploy with env below, `STRAVA_REDIRECT_URI=https://<host>/auth/callback`.
3. Visit `https://<host>/auth/login`, authorize `activity:read_all`.
4. `GET /auth/status` should return `{"linked":true}`.

Phone side: `Mi Fitness` cloud sync ON, `Profile > Third-party data > Strava` linked.
Band workouts appear after Mi Fitness uploads (5-15 min delay).

## Env

| Var | Required | Default | Notes |
|---|---|---|---|
| `STRAVA_CLIENT_ID` | yes | | Strava API app |
| `STRAVA_CLIENT_SECRET` | yes | | Strava API app |
| `STRAVA_REDIRECT_URI` | yes | | `https://<host>/auth/callback` |
| `API_TOKEN` | yes | | Bearer token SteamTimer uses. Long random string. Fail-closed if empty. |
| `USER_TIMEZONE` | yes | `UTC` | IANA zone of the PC, e.g. `America/Santiago`. Must match or logical days drift. |
| `DATA_DIR` | no | `/data` | Volume mount. Holds `strava_token.json` + `activities.json`. |
| `POLL_INTERVAL_MINUTES` | no | `5` | Background Strava poll, 1-60. |
| `WORKOUT_MULTIPLIER` | no | `4` | 4x rule. |
| `MIN_WORKOUT_SECONDS` | no | `600` | Ignore activities shorter than this. |
| `MAX_DOTA_SECONDS` | no | `14400` | Cap per day. `0` = no cap. |
| `COUNT_MANUAL` | no | `false` | Count hand-entered Strava activities. Keep `false`. |

## Run (behind Traefik)

`Dockerfile` listens on `8080`, no TLS (Traefik terminates).

```yaml
services:
  workout:
    build: ./WorkoutService
    environment:
      STRAVA_CLIENT_ID: "123456"
      STRAVA_CLIENT_SECRET: "secret"
      STRAVA_REDIRECT_URI: "https://workouts.example.com/auth/callback"
      API_TOKEN: "long-random-token"
      USER_TIMEZONE: "America/Santiago"
      POLL_INTERVAL_MINUTES: "5"
    volumes:
      - workout-data:/data
    labels:
      - traefik.enable=true
      - traefik.http.routers.workout.rule=Host(`workouts.example.com`)
      - traefik.http.routers.workout.entrypoints=websecure
      - traefik.http.routers.workout.tls.certresolver=letsencrypt
      - traefik.http.services.workout.loadbalancer.server.port=8080

volumes:
  workout-data:
```

## API

* `GET /health` -> `{ok,timeUtc}` (public)
* `GET /auth/login` -> 302 to Strava (public)
* `GET /auth/callback?code=` -> stores tokens (public, Strava calls it)
* `GET /auth/status` -> `{linked,expiresAtUtc}` (public, no secrets)
* `POST /api/sync` -> force poll (Bearer)
* `GET /api/credit?day=2026-11-14` -> `{day,workoutSeconds,dotaSeconds,multiplier,activityCount,updatedAtUtc,activities[]}` (Bearer). `day` omitted = current logical day.
* `GET /api/activities?from=&to=` -> cached dump (Bearer, debug)

SteamTimer polls `/api/credit?day=<logicalDay>` every few minutes:
`Authorization: Bearer <API_TOKEN>`. No credit on error (fail-closed).
