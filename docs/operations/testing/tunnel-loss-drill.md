# Tunnel-loss and fenced recovery drill (AGT-2937)

Implements slice I5 of the Task Server bus Dossier
([docs/task-server-bus/index.html](../../task-server-bus/index.html), AGT-W65),
decision D9, option **bounded-replay** (recommended, decided by the operator on
2026-09-26). Related architecture: AGT-W49
([operations-server-backchannel](../operations-server-backchannel/index.html))
and AGT-W57 ([delivery-chain](../delivery-chain/index.html)).

## What the drill proves

The drill interrupts only the runner-to-Task-Server route of one isolated test
client. It never touches a production tunnel, Task Server, runner service or
workspace. Every fixture uses a temp directory, a temp SQLite store or a temp
bare git origin, and is deleted afterwards. All times are synthetic drill time
from controlled clocks (`2030-01-01`), labelled `synthetic-drill-time` in the
report. They are not data about the operator-reported 2026-09-24/25 tunnel
incident; no dated log of that incident exists in the repository.

Safety deadlines are the production defaults (stop-before is the granted expiry
minus one heartbeat interval). The drill does not change them.

| Scenario | Where | Expected result |
|---|---|---|
| Short interruption inside the granted authority | `runner.Tests/TunnelLossDrillTests.cs` | Worker keeps running; renewal resumes on the same fence; stop-before moves only after a granted renewal. |
| Interruption beyond authority | same | Worker stops exactly at stop-before (`granted expiry - heartbeat`); authority `rejected`; no report replay. |
| Daemon restart after server-side expiry, exact-current identity | same | Renewal answers HTTP 409; re-registration of the exact attempt is `adopted`; only that new server confirmation moves stop-before; the outbox report replays with its original key and applies once. |
| Superseded generation | same | Re-registration is `stale-authority`; authority stays `rejected`; `WaitForConfirmedAsync` refuses; the report is never sent. |
| Superseded generation, git quarantine (MachineBound) | same | Work is preserved only on `agent-studio/quarantine/<runner>/<task>/<attempt>/fence-<n>/<sha>`; push verified by `git ls-remote`; no `agent-studio/results/*` ref. |
| Lost report acknowledgement | same | Server applies the report, the response is lost; after daemon restart the persisted outbox replays the same idempotency key; effect count stays 1. |
| Task Server restart beyond expiry, coding | `task-server.Tests/TaskServerStoreTests.TunnelDrill.cs` | Expired renewal rejected (`lease-expired-process-unknown`); after restart the exact attempt is re-adopted with a new expiry; completion settles once, replay is idempotent. |
| Superseded coding generation | same | After resolution a replacement claim gets a new run and a higher fence; the old identity is not adopted, its completion is rejected, the card stays in `3-progress` for the replacement. |
| Review attempt beyond expiry, exact identity | `task-server.Tests/RemoteReviewAuthorityTests.TunnelDrill.cs` | Review attempt re-adopted after server restart; the report settles once, replay returns the same report. |
| Superseded review generation | same | Explicit re-fenced reclaim raises the fence; the old identity is `stale-authority` and its report is rejected. |
| Already-settled delivery needing integration resume | `backend.Tests/AutoReviewRestartDrillTests.cs` (AGT-2860, AGT-2936) | Integration resumes from the settled review and its sidecar or journal without paying for another review. |

## Run it

From a dev checkout or task worktree (bash):

```sh
JOB_RESULTS_DIR=/path/to/results scripts/tunnel-loss-drill.sh
```

The script runs exactly these commands, each bounded by
`TUNNEL_DRILL_STEP_TIMEOUT_SECONDS` (default 900):

```sh
dotnet test runner.Tests --filter "FullyQualifiedName~AgentRunner.Tests.TunnelLossDrillTests"
dotnet test task-server.Tests --filter "FullyQualifiedName~Tunnel_drill"
dotnet test backend.Tests --filter "FullyQualifiedName~AutoReviewRestartDrillTests"
```

`TUNNEL_DRILL_REPORT_DIR` (default `$JOB_RESULTS_DIR/tunnel-drill`) receives:

- `tunnel-drill-report.md`: one correlated table of outage window, affected
  coding and review attempts, fence and epoch, granted expiry, stop-before,
  worker teardown, quarantine ref, SHA and push status, outbox idempotency key,
  final receipt and terminal state;
- `tunnel-drill.jsonl`: the raw evidence lines per scenario (route drops,
  heartbeat log lines, server decisions);
- `steps.txt` and one log per step. A failing step is reported as `FAIL` with
  its exit code and the script exits non-zero. A broken dependency (git, dotnet,
  disk) is evidence, never a green replay.

The routine suite `dotnet test --filter Category!=MachineBound` runs every drill
scenario except the git quarantine test and the backend integration-resume
drill, which carry `[Trait("Category", "MachineBound")]` because they use real
git processes.

## Behaviour decided in this slice

- `LeaseHeartbeat` re-adoption: when renewal answers HTTP 404/409 and the
  re-registration of the exact attempt is `adopted`, the adoption expiry is
  recorded as a new server confirmation (`DurableLeaseAuthority.Confirm`). Only
  then does stop-before move. Without an adoption expiry the old boundary still
  ends the run. A live worker cannot reach this path after its boundary,
  because the boundary check runs before any renewal attempt.
- Observed, unchanged: the Task Server accepts an exact-identity outbox replay
  (same runner instance, lease and highest fence) on an expired but not
  superseded lease. The runner never sends it without a confirmed authority,
  so the drill re-adopts first.
