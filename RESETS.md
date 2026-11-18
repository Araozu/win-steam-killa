# SteamTimer usage resets

Agent-maintained history of full usage wipes. Each entry is a state-version
bump that discards `state.json` on next deploy (tightenings only — never to
forgive overuse, never on loosening).

| Date | Version | Reason |
|---|---|---|
| 2026-10-18 | v4 -> v5 | Dota: 8h weekly budget + forbidden Mon/Wed/Fri (tightening). |
| 2026-10-25 | v5 -> v6 | Weekday play windows 05:30–06:15 + 20:30–22:00, weekends free (tightening). |
| 2026-11-15 | v6 -> v7 | Weekday workout-gated Dota: Mon–Fri Dota must be earned via workout credit (8h weekly cap, fail-closed); weekends keep the free grant (tightening). |
