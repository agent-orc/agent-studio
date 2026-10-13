# Occurrences

Chronological log. Newest at the top. UTC timestamps. One row per observation.

| When (UTC) | Task / context | Agent / CLI | Affected paths | Notes |
|---|---|---|---|---|
| 2026-10-12T20:06:00Z | Re-measurement while drafting AGT-3092 | curl, Windows PowerShell 5.1, Node 24 | `:5031/healthz`, `:4011/` | curl `time_connect` 0.203-0.214 s via `localhost`, 0.0009-0.0014 s via `127.0.0.1`; PowerShell 2.06-2.09 s per fresh process. |
| 2026-10-12T19:30:00Z | Finding `ui/api-6` against the running Stable | curl | `:5031/healthz`, `:4011/` | 0.204-0.221 s via `localhost`, under 1 ms via `127.0.0.1`. `netstat -ano` shows only `127.0.0.1` listeners on 5031 and 4011. |
