# Develop to main promotion

`develop` is the work line. `main` is the release line. The standard promotion
train fixes the fetched `develop` tip as its exact candidate, runs the complete
blocking gate against that commit, publishes an annotated release marker with
`main`, and hands the new `main` SHA to the external Stable deploy watcher.

Use [the promotion command](../../scripts/release/promote-develop-to-main.sh)
for this repository. It is an operator command, not a worker pipeline step.
Managed task agents must leave commit, merge, tag, and push ownership to the
platform and operator boundary described in the
[commit and push doctrine](./git/commit-push-doctrine.md).

## Safety contract

The command fails closed when any of these facts is false:

- the operator checkout is clean and its `HEAD` equals fetched
  `origin/develop`;
- an optional required convergence commit is reachable from `develop`;
- the annotated `release/*` marker does not already exist;
- the candidate is a descendant of `main` before the gate and remains a
  descendant of the freshly fetched `main` immediately before the push;
- every command in
  [promotion-full-gate.sh](../../scripts/release/promotion-full-gate.sh) passes
  against the exact candidate commit;
- the gate emits its completion marker and leaves the candidate checkout clean;
- the server accepts one atomic push containing both `main` and the annotated
  release marker; and
- remote verification resolves both refs to the tested candidate SHA.

An advance of `develop` during the gate is informational: the train logs the
new tip and still promotes the gated candidate. The newer commit waits for the
next train. A concurrent `main` update is safe only when the current `main`
remains an ancestor of the candidate. The command never force-pushes, and the
atomic non-force push closes the race after the final fetch. A red or incomplete
gate has no override. A blocked run removes the temporary worktree while the
durable evidence directory remains. If tag creation succeeded before an atomic
push failure, its local ref may remain in the operator checkout and should be
removed before retrying the same marker.

## Operator checklist

1. Ensure no other operator is promoting `main`. Normal Agent Studio pickup and
   integration may continue; this train promotes only its gated start candidate.
2. Update the operator checkout to the current `origin/develop`. Confirm
   `git status --short --branch` is clean.
3. Preview the graph and manifest:

   ```sh
   ./scripts/release/promote-develop-to-main.sh \
     --dry-run \
     --required-ancestor 0f5372fce
   ```

4. Review `manifest-summary.tsv`, `manifest-commits.tsv`,
   `main-only-patch-review.txt`, `candidate-whitespace-review.txt`, and the
   candidate commit in the reported evidence directory.
   Historical committed whitespace is review evidence rather than a release
   veto; the mandatory build and test gate remains blocking. This file-level
   manifest is the manual bridge until the in-product REL-1 surface adds live
   task acceptance groups. It does not claim that integration equals
   acceptance.
5. Run the promotion with a deliberate release marker:

   ```sh
   ./scripts/release/promote-develop-to-main.sh \
     --execute \
     --required-ancestor 0f5372fce \
     --tag "release/$(date -u +%Y%m%d-%H%M%SZ)"
   ```

   The command supplies a tagger identity for the annotated marker and does not
   depend on the host's Git identity. Override the defaults when an operator or
   automation-specific identity is required:

   ```sh
   PROMOTION_TAGGER_NAME="Release Automation" \
   PROMOTION_TAGGER_EMAIL="release-automation@example.invalid" \
     ./scripts/release/promote-develop-to-main.sh \
       --execute \
       --tag "release/$(date -u +%Y%m%d-%H%M%SZ)"
   ```

   `PROMOTION_TAGGER_NAME` defaults to `Agent Studio Promotion`, and
   `PROMOTION_TAGGER_EMAIL` defaults to
   `promotion@agent-studio.invalid`.

6. Verify `promotion-record.json` says `status=promoted`, `gate=passed`, and
   `atomicPush=true`. Verify the remote `main` and peeled tag resolve to the
   recorded `candidateSha`.
7. Pass `--project <name>` on the `--execute` run (an Agent Studio project
   name, e.g. `Agent Studio`) so the atomic push step also requests branch
   reclaim - see "Branch reclaim after promotion" below. Omitting it only
   skips that request; the branches are still picked up by the next periodic
   sweep.
8. Monitor the deploy handoff. The deploy cron described below detects the new
   `main`, waits for Stable to become safe to restart, runs `update-stable.sh`,
   and verifies the deployed checkout.

The 1 August pre-promotion convergence at `0d8d6794a` merged the remaining
`main` fixes into `develop`. Any later `main`-only change must likewise be
converged into `develop` before promotion. The exact-SHA train does not create a
merge commit and cannot override a divergent branch graph.

## Mandatory full gate

The gate uses the same honest-CI principle as tag-bound releases, but it does
not publish versioned binaries. It runs, in order:

1. .NET restore;
2. frontend `npm ci` and the critical production dependency audit;
3. release shell contract tests, including promotion and deploy-watcher tests;
4. the .NET Release build;
5. every non-machine-bound .NET test in the solution;
6. frontend lint and type-check;
7. frontend unit tests; and
8. the production frontend build.

Machine-bound suites remain separately scheduled evidence and are never hidden
inside the normal green result. The promotion gate excludes them explicitly,
matching the repository test contract and release workflow.

## Release marker and evidence

The annotated `release/<UTC timestamp>` tag is a promotion marker. It is not a
`stable/*` freeze and does not trigger the `vX.Y.Z` asset workflow. A stable
claim still requires the separate stable-freeze evidence described in
[release semantics](../concepts/release-semantics.md) and
[the Stable release contract](./stable-release-contract.md).

By default the command writes evidence beneath Git's local
`promotion-results` path. Set `PROMOTION_EVIDENCE_DIR` or pass
`--evidence-dir` to use an operator-owned durable location. The record includes
the start `develop`, previous `main`, exact candidate, required ancestor,
gate-script blob, gate result, tag, and atomic-push result. Logs include the full
gate output, tag creation output, and remote push response. A tag or push
failure after a passed gate writes `status=blocked-tag` or
`status=blocked-push`, retains `gate=passed`, and records the command error so
the evidence directory remains complete.

## Branch reclaim after promotion

AGT-2793: `develop`/`main` promotion is the last point at which a task's
`task/*`, `runner/*`, `delivery/*`, and `agent-studio/results/*` refs become
eligible for deletion (see "Branch cleanup" in
[task-integration-and-merge-workflow.md](../concepts/task-integration-and-merge-workflow.md)).
Promotion itself runs as a runner-host shell script, outside the backend
process, so it has no in-process transition to hook the reclaim trigger off
of. Instead, once the atomic `main` + tag push is verified (`write_record
promoted ...`), the script best-effort calls
`POST /api/git/branch-reclaim/promotion?project=<name>` against the backend
(`ATP_API`, default `http://127.0.0.1:5031`) when `--project` was given. That
endpoint resolves the project's configured repository and runs
`BranchReclaimTriggerService.ReclaimAfterPromotionToMain`, which sweeps all
six ref namespaces for every project (not just the one that promoted) and
records evidence the same way the integration and archive triggers do. A
failed or skipped call is logged in `branch-reclaim.log` inside the evidence
directory and never fails or reverts the promotion; the next periodic
`GitBranchRetentionHostedService` sweep (`GitRetention:IntervalHours`, default
24h) covers anything missed.

## Deploy cron handoff

The one-shot Stable watcher now supports `ATP_RESTART_TRIGGER=main-advance`.
This is the handoff for the repository-backed operator Stable checkout. It does
not replace the immutable `vX.Y.Z` plus build-manifest deployment contract for
packaged installations. Run it from a host scheduler with an external lock so
long updates cannot overlap:

```cron
* * * * * flock -n /var/lock/agent-studio-main-deploy.lock env ATP_RESTART_TRIGGER=main-advance ATP_WORKSPACE=/srv/agent-taskboard-workspace ATP_STABLE_CHECKOUT=/srv/agent-taskboard-stable ATP_UPDATE_SCRIPT=/srv/agent-taskboard-dev/scripts/update-stable.sh /srv/agent-taskboard-dev/scripts/supervisor/restart-stable-after-batch.sh >>/var/log/agent-studio-main-deploy.log 2>&1
```

The tick is a no-op while Stable already matches remote `main`. When `main`
moves, it still requires runner-idle state and the existing bounded merge-gate
drain before invoking the updater. If Stable remains behind after a successful
update or a transient remote check fails, the structured restart log names the
condition and a later cron tick retries. The watcher never changes task state.

## Failure and recovery

- Candidate-ancestry or gate failure: inspect the evidence, converge the branch
  if needed, fetch the new tips, and start a new run. A `develop` advance alone
  does not invalidate a gated candidate.
- Atomic push failure: remote `main` and the release marker remain unchanged.
  The record reports `status=blocked-push`, `gate=passed`, and the push error.
  Fix credentials or branch policy, remove the unpushed local marker if it is
  still present, then rerun from fresh refs.
- Annotated tag failure: remote `main` and the release marker remain unchanged.
  The record reports `status=blocked-tag`, `gate=passed`, and the tag error.
  Correct the configured tagger identity or local repository problem, then
  rerun from fresh refs without repeating diagnosis from an incomplete evidence
  directory.
- Deploy failure: the promotion remains a valid release fact. Diagnose the
  external updater and use its rollback procedure; do not rewrite `main` or
  move the release marker.
- Released regression: revert the offending change through normal `develop`
  work and run a new promotion. Reserve an immutable Stable rollback for the
  deployment incident response path.
