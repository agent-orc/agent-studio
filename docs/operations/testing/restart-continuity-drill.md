# Restart continuity release drill

Run this release gate on the Windows Studio after the candidate has been
integrated and while stable is behind the candidate. It deliberately uses the
real UpdateService, one local coding run, and one Remote coding run. A developer
sandbox is not a substitute because the proof is about the deployed process
supervisors, Windows process behavior, and the registered Agent Host.

## Prepare the two subjects

Create two harmless test tasks whose commands take at least five minutes and
produce an unambiguous Result. Configure one project for local execution and
the other for the test Agent Host. Start both and wait until both cards are in
Progress with an active execution. Do not use production work as a drill
subject.

From Git Bash in the integrated checkout, set:

```sh
export STUDIO_URL='http://127.0.0.1:5031'
export UPDATE_URL='http://127.0.0.1:5039'
export LOCAL_PROJECT='Local restart drill'
export LOCAL_TASK_ID='RST-101'
export REMOTE_PROJECT='Remote restart drill'
export REMOTE_TASK_ID='RST-201'
# export UPDATE_TOKEN='only-when-the-service-requires-it'
```

Run the gate and retain its complete output with the release evidence:

```sh
bash scripts/restart-continuity-drill.sh \
  | tee "restart-continuity-$(date -u +%Y%m%dT%H%M%SZ).log"
```

The script refuses to trigger the update unless the first task reports a local
active execution and the second reports a Remote active execution. It then:

1. invokes the real `POST :5039/update/trigger` endpoint;
2. tolerates the expected period in which the Studio API is unavailable;
3. fails immediately if either timeline reports `run_lost_across_restart`;
4. requires a new `agent_run_finished` event for both subjects;
5. requires the local task timeline to contain
   `run_continued_after_restart`; and
6. requires UpdateService history to settle the same update run as `ok`.

Capture the local card's Timeline tab while the
`Continuing after restart` event is visible. Store that PNG beside the drill
log. Archive or delete the two test subjects after the evidence has been
copied.

## Readiness-only check

Before integration, validate the portable script without inventing a live
target:

```sh
bash -n scripts/restart-continuity-drill.sh
bash scripts/restart-continuity-drill.sh --check-readiness
```

The readiness check proves the operator dependencies and records that the live
run is intentionally a post-integration release step. It does not claim that a
restart occurred.
