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
5. every non-machine-bound .NET test in the solution, including the
   legacy-plane completion contract test
   (`backend.Tests/LegacyRunnerCompletionContractTests.cs`);
6. the [deployment regression scenario](testing/deployment-scenario.md) at
   `smoke` level, which runs the coding attempt on the v1 plane and on the
   legacy runner plane the production fleet uses;
7. frontend lint and type-check;
8. frontend unit tests; and
9. the production frontend build.

A gate that exercises only the v1 plane is not evidence for the fleet: Stable
0.9.3 passed it and then had every production completion rejected on the
legacy plane (AGT-2985).

Machine-bound suites remain separately scheduled evidence and are never hidden
inside the normal green result. The promotion gate excludes them explicitly,
matching the repository test contract and release workflow.

## Gate capacity window

The train runs on a runner host next to the coding and review units. On
27 September 2026 (release 0.9.3, trains `release/20260927-103354Z` and
`release/20260927-112445Z` on agent-runner-01) a busy fleet drove the load
average to 40 to 114 on 12 cores: `OrchestratorApi.Tests` took 42 instead of 9
minutes and the deadline-bounded deployment regression scenario failed twice
without a defect. Its step budgets are part of the deployment contract and are
not raised to hide an oversubscribed host. Instead the train reserves capacity
for its gate (AGT-2982).

[release-gate-window.sh](../../scripts/release/release-gate-window.sh) wraps the
full gate and, on every `--execute` run:

1. records the current `CPUQuota` of `agent-runner.service` and
   `agent-runner-review.service` (`systemctl show -p CPUQuotaPerSecUSec`);
2. sets a runtime train-window quota with
   `sudo -n /usr/bin/systemctl set-property --runtime <unit> CPUQuota=<N>%`.
   By default the window leaves `RELEASE_GATE_RESERVED_CORES=5` cores to the
   gate and splits the rest two sevenths to coding and five sevenths to
   review, each at least one core. On 12 cores that is `200%` coding and `500%`
   review, the same window the operator applied by hand on 27 September.
   `RELEASE_GATE_CODING_QUOTA` and `RELEASE_GATE_REVIEW_QUOTA` override the
   values. The window never raises a unit above its recorded quota;
3. refuses to start hot: when the 1-minute load is above
   `RELEASE_GATE_MAX_LOAD_FACTOR` (default `2`) times the core count, it polls
   every `RELEASE_GATE_POLL_SECONDS` (default `15`) for at most
   `RELEASE_GATE_SETTLE_SECONDS` (default `300`) while the throttle takes
   effect, then starts the gate regardless and logs that it started hot;
4. runs the gate in its own process group and measures the load at its start
   and end and its duration; and
5. restores the recorded quotas from a trap on every exit path: a green or red
   gate, a helper error, and `INT`, `TERM`, or `HUP`. On a signal the helper
   first stops the whole gate process group, not only its immediate child: it
   sends `SIGTERM` to the group, sends `SIGKILL` to whatever is still alive
   after `RELEASE_GATE_STOP_GRACE_SECONDS` (default `30`), and restores the
   quotas only once no process of the group remains, so no orphaned test or
   build process keeps running at full runner load. An unlimited unit is
   restored with the empty `CPUQuota=` reset.

`RELEASE_GATE_WINDOW` selects the policy: `auto` (default) applies the window
when the units are loaded and, when a quota cannot be set (for example the
sudoers rule is missing), rolls back what it applied, logs a warning, and runs
the gate unthrottled. `required` refuses to run the gate instead; the train
then records `status=blocked-gate-window`, `gate=not-run`, and leaves `main`
unchanged. `off` never touches the units but still records the load evidence.
A host without the runner units records `mode=skipped`.

While the window is active the review daemon sees the lower role `cpu.max`, so
its claim clamp (`floor(role quota cores / 2)`) admits fewer review workers
instead of starving admitted ones. Running workers keep going at reduced
speed. The restore writes the recorded value back as a runtime property: the
effective quota is the pre-train value, and the runtime drop-in below
`/run/systemd/system.control/` disappears at the next reboot. If onboarding
changes a persistent role quota before then, remove
`/run/systemd/system.control/<unit>.d/50-CPUQuota.conf` and run
`systemctl daemon-reload` as root, or reboot, so the runtime value does not
shadow the new persistent one.

### Sudoers rule

The host sudoers policy
[deploy/agent-host/sudoers.d/agent-runner](../../deploy/agent-host/sudoers.d/agent-runner)
grants the service account exactly this call shape, installed by
`sudo ./scripts/harden-agent-runner-host.sh --apply`:

```sudoers
Cmnd_Alias AGENT_RELEASE_GATE_WINDOW = \
    /usr/bin/systemctl ^set-property --runtime agent-runner[.]service CPUQuota\=([1-9][0-9]{0\,5}%)?$, \
    /usr/bin/systemctl ^set-property --runtime agent-runner-review[.]service CPUQuota\=([1-9][0-9]{0\,5}%)?$
```

The argument regular expression needs sudo 1.9.10 or newer (Ubuntu 24.04 ships
1.9.15). It admits only a runtime `CPUQuota` of a whole percentage, or the empty
reset, on the two runner units: no persistent change, no other property, and no
other unit. A regular expression is required because the restore writes back
whatever quota onboarding derived for the host, which an enumerated list cannot
cover. Verify the rule from the operator account with `sudo -n -l`. Agent CLIs
run with `NoNewPrivileges=true` and cannot use it.

### Why a quota window and not a priority slice

The card allowed a dedicated systemd slice with a `CPUWeight` well above the
runner units instead. The quota window was chosen because:

- `CPUWeight` only arbitrates between sibling cgroups. The operator runs the
  train from a login session in `user.slice`, while the runner units live in
  `system.slice`. A high-weight slice below `user.slice` competes only with
  other user sessions; to outrank the runner units the gate would have to run
  as a system unit, which means granting `systemd-run` through sudo. That is an
  arbitrary root command, far wider than two runtime `CPUQuota` assignments.
- A weight shares CPU under contention but does not shed load. A quota also
  lowers the review daemon's admission clamp, so fewer review workers start
  instead of many starving ones tripping their no-CPU-progress and silence
  watchdogs.
- The quota window is exactly the manual mitigation that worked on
  27 September, now automated with a restore on every exit path.

### Evidence

`gate-window.env` in the evidence directory is the helper's raw record.
`promotion-record.json` carries the attributed fields: `hostLoadAtGateStart`,
`hostLoadAtGateEnd`, and `gateDurationSeconds` (numbers, `null` when the gate
did not run), `appliedQuotas` (per unit, the recorded and applied `CPUQuota`),
and `gateWindow` (`mode`, `cpuCount`, `reservedCores`, `loadThreshold`,
`loadBeforeWait`, `loadWaitSeconds`, `quotasRestored`, `gateExit`). A slow or
red gate with a high `hostLoadAtGateStart` or `loadWaitSeconds` at the bound
points to host load rather than a defect. `quotasRestored=failed` is also
logged as a warning in `promotion.log`; restore the recorded values from
`appliedQuotas` by hand in that case.

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
gate-script blob, gate result, tag, atomic-push result, and the gate capacity
window evidence described above. Logs include the full
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

- Gate failure on a loaded host: compare `hostLoadAtGateStart`,
  `hostLoadAtGateEnd`, and `gateDurationSeconds` with a quiet run before
  debugging the failing test. Confirm `gateWindow.mode=applied`; `unavailable`
  means the sudoers rule is missing and the gate ran unthrottled.
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
- Runner host deploy: `agent-runner-deploy` waits up to ten minutes after the
  restart for one accepted completion from the new Coding service invocation
  and otherwise prints the rollback command to the previous runner release.
  Follow the
  [post-restart completion check](setup/linux-runner-host.md#post-restart-completion-check)
  before you declare a runner release deployed.
- Released regression: revert the offending change through normal `develop`
  work and run a new promotion. Reserve an immutable Stable rollback for the
  deployment incident response path.
