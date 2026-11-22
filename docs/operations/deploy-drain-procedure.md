# Deploy without lost work: drain, quiet point, server, hosts

A deploy must not end running work against a card. On 9 November 2026 Deploy 36
(18:03 to 18:42 UTC) and Deploy 37 (23:06 to 23:10 UTC) followed the documented
order and still lost work:

| Card | What the deploy cut | Fix (AGT-3647) |
|---|---|---|
| AGT-3591, WEB-37 | A review settled as `ReviewInfra / ExecutorRestarted` ("could not adopt the persisted review process ... is not running"), 7 of 15 commands lost | The review daemon relaunches a dead worker under the same lease; it resumes from its checkpoint |
| AGT-3584 | "GateInfra: per-task gate interrupted 2 times", card escalated | A start charged by an earlier API instance is refunded; restarts never spend the budget |
| AGT-3403 | A review container on the old image claimed a new review during the rolling update | A drained review executor claims nothing; the deploy drains every host first |
| Batch 9960e7c6 | Startup recovery failed once and held three members for 3.5 hours | Each interrupted batch is recovered on its own; one failure no longer stops the others |

The procedure is scripted in [`deploy/networked/deploy.sh`](../../deploy/networked/deploy.sh);
the manifest and host commands are described in
[setup/networked-deploy.md](setup/networked-deploy.md). To make each deploy
visible in the Studio itself (live release, cards, failed or stalled step),
report its start, steps and end as described in
[instance-deploy-records.md](instance-deploy-records.md).

## Guided review-host update in Execution Hosts

When a review role runs another commit or image than the server expects,
Execution Hosts shows **Update review host** with the expected commit and
immutable image digest. Its guided progress drains that runner identity through
the client API and reads the lease-authority drain status. An active review
refuses the host switch. At the quiet point, use the supported networked deploy
procedure below for the image build and host replacement. The UI does not run
SSH or Docker commands from the server.

Select **I am starting the host switch** immediately before the operator deploy
steps. **Check new heartbeat** waits for a review-role report later than that
selection with the exact expected commit and image digest. A successful deploy
command or a matching older report does not pass verification. After proof,
**Resume review host** lifts the client drain and checks that admission is open.
Before starting the switch, **Cancel and resume** lifts the drain without
recording a completed update. It posts `POST /api/clients/{id}/deploy-cancel`,
which the server refuses with 409 and the current drain status once any
operator recorded the switch start; the drain and the switch start then stay,
and the panel moves to **Check new heartbeat**.
The full networked deploy script also drains and resumes its manifest's host
identities. Its own idle and image verification remain required; the UI's fresh
heartbeat provides a separate check of what the runner actually reports.

### Review roles without a client identity

The source-built first-run Compose profile starts its review role without
`RUNNER_CLIENT_ID`. Its review executor registration does not create a record
in the Studio client identity store, so the guided panel cannot drain it yet.
Register that role with the Studio API before using the panel:

```sh
curl -sS -X POST http://127.0.0.1:4011/api/clients/register \
  -H 'Content-Type: application/json' \
  -d '{"displayName":"distributed-review-runner","kind":"service"}'
```

Check that the returned `id` equals the review role's `RUNNER_ID` shown in
Execution Hosts. A different id will not drain that role; resolve the name
collision before proceeding. Select **Check identity again** in the panel,
then follow its drain, quiet point, host switch, verification, and resume
steps. For this Compose profile, rebuild and recreate only the review service
at the host switch after the panel confirms a quiet point:

```sh
AGENT_STUDIO_VERSION="$(cat VERSION)" docker compose --profile dev up -d --build --no-deps agent-host-review-distributed-dev
```

Keep the client drain in place until the replacement reports the expected
commit and image digest in the panel. Networked installations whose review
role already has a registered client identity use the normal guided flow.

## Order

1. **Drain every work host.** For each runner identity a coding or review host
   runs (`drainClients` in the manifest), `studio-op host drain <client-id>`
   sets the server-side drain. A drained runner identity gets no new coding
   lease (`runner is draining`) and no new review (`executor-draining`).
   Running work continues and keeps renewing its lease.
2. **Wait for a quiet point**, polling every `quietPoint.pollSeconds` until
   `quietPoint.deadlineSeconds` (default 45 minutes). The point is quiet when:
   - no drained host still runs a review (`studio-op host status <client-id>`
     lists its leased attempts; coding runs may continue, the replacement
     daemon adopts them);
   - no batch is in its first `batchFirstMinutes` (formed, assembling,
     assembled, or running for less than that) or its last
     `batchLastMinutes` (green, publishing, or past
     `batchExpectedMinutes - batchLastMinutes` when an expected duration is
     set);
   - no integration push is pending and the OP-6 deploy preflight is clear.

   A deadline without a quiet point stops the deploy and resumes every drained
   host. Nothing has been changed at that point.
3. **Open the OP-6 window** (`studio-op deploy plan`), close server admission,
   publish the gate worker, and check the quiet point again right before the
   switch.
4. **Switch the server**, then the engine.
5. **Update the hosts one at a time.** Each coding host is installed, checked
   and model-probed, then resumed (`studio-op host resume <client-id>`). Each
   review host is rebuilt, checked idle, recreated and verified, then resumed.
   A host never claims on the old release after the server switched, and never
   claims before its own update is verified.
6. **Resume server admission.**

Rollback resumes every drained runner identity after the restore.

## What still protects work that a deploy cuts

The quiet point avoids most cuts; these rules handle the rest.

- **Review service restart.** A daemon stop hands the slot over instead of
  settling it. When the replacement finds a valid lease and no live worker, it
  starts the worker again within the same attempt, at most
  `DurableReviewProcess.MaximumWorkerRelaunches` (2) times. The worker resumes
  from `review-progress.json`, so finished commands do not run again. A stop
  during workspace preparation prepares again. A re-claimed attempt whose old
  lease expired runs again from a fresh workspace. Only a worker that dies
  again after its relaunches is settled as `ExecutorRestarted`.
- **API restart under a per-task gate.** The gate start carries the API
  instance that charged it (`FallbackGateRunBy`). A later instance refunds that
  start (`GateCancellationPolicy.ChargedStartsAfterServiceRestart`) and runs the
  gate again. This holds also when the process was killed before its stop
  handler ran. A gate that throws inside one running instance keeps its charge.
- **API restart under a batch.** Startup recovery abandons the interrupted
  batch and returns its members to planning. An abandoned batch charges no
  member.

## Timing record

The journal keeps one entry per step in the deploy duration format:
`step`, `startedAtUtc`, `endedAtUtc`, `durationSeconds`, `status` and, on
failure, `error`. Host steps are named `host_drain:<host>:<client>`,
`host_resume:<host>:<client>` and `host_resume_after_abort:<host>:<client>`.
The `quiet_point` step's duration is the wait. `quietPoint` in the journal
records the number of checks and the last blockers. Copy the `steps` array into
the deploy durations table after each deploy.

## Manual equivalent

Without the networked deploy script, run the same order by hand:

```sh
for client in agent-runner-01 agent-runner-01-review kori-review-1 kori-review-2 kori-review-3; do
  studio-op host drain "$client"
done
studio-op host status kori-review-2     # repeat until "drained" for every review identity
studio-op preflight deploy              # repeat until clear
studio-op triage                        # no batch formed, assembling, green or publishing
# deploy the server, then each host; after each host is verified:
studio-op host resume <client-id>
```

The identities above are examples; use the runner ids your installation shows
on the Execution Hosts page.
