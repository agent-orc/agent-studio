# Occurrences

Chronological log. Newest at the top. UTC timestamps. One row per observation.

| When (UTC) | Task / context | Agent / CLI | Affected paths | Notes |
|---|---|---|---|---|
| 2026-09-15T10:26:00Z | AGT-2831 reproduction on a review-class Linux host | `scripts/review-build-server-isolation-probe.sh 4` | `/tmp`, attempt `TMPDIR` | Four concurrent attempt-shaped builds: 382 `MSBuild*` sockets and 1 compiler socket under `/tmp`, 0 under any attempt `TMPDIR`, 27 reusable nodes adoptable by any attempt. A live `VBCSCompiler` held `PWD` inside an unrelated attempt's gate workspace. |
| 2026-09-15T02:00:00Z | `agent-runner-01-review` at `RUNNER_MAX_PARALLELISM=4` | remote review executor | backend test suite | Concurrent review workers deadlocked. `dotnet test` at 0.1 percent CPU for 87 minutes; slots held until the 2 h budget. Host reduced to 2 review slots in `/etc/agent-runner/runner-review.env`; review became the bottleneck with 20+ cards waiting. |
