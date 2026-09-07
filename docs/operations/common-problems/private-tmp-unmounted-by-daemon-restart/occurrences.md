# Occurrences

Chronological log. Newest at the top. UTC timestamps. One row per observation.

| When (UTC) | Task / context | Agent / CLI | Affected paths | Notes |
|---|---|---|---|---|
| 2026-09-07T02:51:00Z | AGT-2716 baseline preparation | agent-runner-01 review unit | `/tmp/.dotnet.XXXXXX` | `dotnet restore` failed with `mkdtemp ... ENOENT` right after the 02:46 restart. Classified `ReviewInfra`, so it retried, but the retry burned another full baseline run. |
| 2026-09-07T01:29:00Z | AGT-2709, AGT-2711, QS-82, QS-96 verification | agent-runner-01 review unit | `/tmp/MSBuild<pid>` | `dotnet test` failed with `MSB1025` / `SocketException (99)` after the 01:10 and 01:12 restarts. Zero tests executed; graded `build-tests: 1 new failures: <unparsed failure in verify-2>` and routed to Human Review as "Delivery gate failed". |
| 2026-09-07T01:10:00Z | Review unit restart with slots busy | agent-runner-01 | `/proc/2593715/mountinfo` | First direct proof: the surviving `dotnet test` worker held `/tmp/systemd-private-...-agent-runner-review.service-WhdhIS/tmp//deleted` mounted at `/tmp`. |
| 2026-09-06T17:14:00Z | Coding unit restart | agent-runner-01 | `/tmp` | Same configuration on the coding unit. Ten stale `systemd-private-...-agent-runner.service-*` directories remained under `/tmp`. |
