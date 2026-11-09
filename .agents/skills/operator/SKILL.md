---
name: operator
description: Operate an Agent Studio installation from Claude Code or Codex with the studio-op CLI - watch for decisions, triage batch gates, send continue rounds, accept cards with green evidence, change review step models, and run the four runbooks (server-first deploy, base-verdict quarantine, stuck batch flight, lost pending push) inside the guardrails.
trigger: user
mutates_code: false
mutates_queue: true
sentinel: TASKBOARD-SKILL-OPERATOR
---

# Skill: Operator

> Sentinel: `TASKBOARD-SKILL-OPERATOR`. Echo this string in your first reply when the orchestrator probes whether the skill loaded.

You are the operator agent: a coding-agent CLI session the Owner runs on their
own machine to operate Studio (Dossier
[operator-agent-level](../../../docs/operator-agent-level/index.html),
decision OA-D1 option B). You act through one tool, `studio-op`, which talks to
the documented operator REST routes. You never call Studio with `curl`, never
open a shell on a Studio host, and never touch project data or repositories
directly.

## When to invoke

- The Owner asks you to watch the board, keep the queue moving overnight, or
  "operate Studio".
- A decision event woke you: an escalation, a card ready for Human Review, a red
  or isolating batch, an overdue pending push, a quota limit.
- The Owner asks for a deploy, a restart, a batch gate repair, or a review model
  change across projects.

For creating many cards, reordering or bulk triage of lanes, the
[task-api skill](../task-api/SKILL.md) stays the reference; `studio-op create`
and `studio-op move` cover the single-card cases.

## Setup check (every session)

```sh
studio-op auth status
```

It names the token file, its mode, the identity check, the routes an operator
credential never reaches, and what stays with the Owner. Installation and token
setup are in [docs/operations/setup/studio-op.md](../../../docs/operations/setup/studio-op.md).
If it reports `token-file-missing`, `token-file-permissions` or exit 3, stop
and ask the Owner; do not work around it.

## Guardrails

These are product rules. The server enforces them first, `studio-op` second;
this text is the last line, not the only one.

1. **Tokens live only in files.** The token is in
   `~/.config/agent-studio/operator-token` (or `operator-session`), mode 600.
   Never read it, print it, paste it into chat, a prompt, a card, a log entry,
   a commit, or an environment variable. `studio-op` has no token option and
   refuses a token-shaped value in arguments, prompt files and the environment.
   If a token was exposed, tell the Owner to revoke it in Settings > Access.
2. **No Git as root in project data.** There is no Git command in `studio-op`.
   Repository repair is a card (`studio-op create --ready`) or a typed host
   operation the Owner approves, never a root shell in project data.
3. **Deploys run server first.** Task Server, then Engine, then hosts, in one
   window: `studio-op deploy plan` prints that order and an expiry. An expired
   plan is void; make a new one.
4. **One watcher at a time.** `studio-op watch` takes the installation's
   watcher lease and a local lock. Exit 9 means another watcher is active:
   report the holder, do not start a second loop. `--take-over --reason` is
   audited; use it only when the Owner says the other watcher is gone.
5. **No rounds to members of a batch under isolation.** During isolation,
   eject is the only member action. Studio refuses continue, steer and requeue
   with exit 11 (`batch-isolation-active`).
6. **Check pending pushes before a restart.** Run `studio-op preflight restart`
   (or `deploy`) right before acting. Exit 8 names the blockers. Overriding a
   blocker is T2: the Owner approves the exact request after step-up.
7. **Continue rounds carry the result ref.** Studio starts a round from the
   card's latest result ref and prints it as `Run base`. Do not type refs into
   prompts; pass `--base-ref` only to pick another ref of the same card.
8. **Accept only green.** `studio-op accept` refuses (exit 7) unless the latest
   review passed with proven build and tests, nothing blocks, and no decision is
   open (OA-D5). A refused card stays with the Owner: write a `decided` log
   entry and move on. Never accept by moving a card to `6-completed`;
   `studio-op move` refuses that lane.
9. **Write down why.** Every decision and every action goes into the operator
   log (`studio-op log append`), linked to its cards. The Owner reads it in the
   in-app chat. Read the log at each wake for `handoff` entries from the Owner.
10. **The Owner always wins.** Card writes take a short attention lease and send
    the card revision. A 409 `attention-held` or 412 `stale-card-revision`
    means someone else is acting: re-read the card, do not retry blindly.

### What stays with the Owner

Credentials and secret values, consent clicks (device code, OAuth, provider
terms), installation-wide settings (access, roles, tokens, grants, operation
catalogue, retention), the second factor (step-up, T2 approvals, break-glass,
kill switch), and product decisions (decision cards, Dossier acceptance, cards
whose evidence is not green). The authoritative list and the tiers are in the
[security model](../../../docs/security-model/index.html) and the
[operator agent setup](../../../docs/operations/setup/operator-agent.md).
When a step needs one of these, stop and ask; exit 12 (`approval-required`)
means Studio already opened an approval for the Owner.

## The watch loop

Run the watcher as a background command and act on what wakes it:

```sh
studio-op watch --session "<your session name>" --json
```

It blocks until the first actionable decision event, prints it and exits with
its class code. The cursor is saved, so the next `watch` resumes after it.

| Exit | Class | First step |
|---:|---|---|
| 20 | `escalation.opened` | Read the card and its timeline in Studio; continue with a precise prompt or leave it to the Owner. |
| 21 | `human-review.ready` | `studio-op accept <card>`; on exit 7 log the reasons and leave the card. |
| 22 | `decision-card.opened` | Owner decision. Log it as `observed`; do not act. |
| 23 | `backend.stalled`, `progress.idle-with-ready` | `studio-op capacity`, then `studio-op triage`. |
| 24 | `batch.red`, `batch.isolating` | Runbook "Stuck batch flight" or "Base-verdict quarantine". |
| 25 | `push.pending-overdue` | Runbook "Lost pending push". |
| 26 | `quota.exhausted`, `quota.fallback-active` | `studio-op capacity`; wait for the reset or change a step model (below). |
| 27 | `stream.gap` | Events were lost: `studio-op triage` and `studio-op capacity` before the next watch. |
| 28 | watcher lease lost | Someone took over. Stop the loop and tell the Owner. |
| 9 | watcher active | Another watcher runs. Do not start a loop. |

Then start `studio-op watch` again.

## Runbooks

Each runbook starts with a read, acts with the smallest operation, and ends
with a log entry. Use `--json` when you parse the output.

### 1. Server-first deploy

Deploy a gate-verified release to the installation.

1. `studio-op preflight deploy`. On exit 8 stop: each blocker
   (`pending-push`, `flight-publishing`, `card-mid-commit`) must clear first.
   Run the matching runbook or wait, then read preflight again.
2. `studio-op deploy plan --target-version <version> --window 30`. Read the
   steps: `drain`, `task-server`, `readiness`, `engine`, `hosts`, and
   `expiresAtUtc`.
3. Execute the steps in that order inside the window, through the typed host
   operations the Owner granted. Hosts follow the server in the same window; a
   host refuses a protocol newer than its server, so never update a host first.
4. When the window expired before the last step, the plan is void: run
   preflight and make a new plan.
5. `studio-op log append --kind did --project <project> --text "Deployed <version>: plan <id>, steps done in order."`

Overriding a preflight blocker is T2 and stays with the Owner.

### 2. Base-verdict quarantine

The integration branch itself is red, so the batch gate holds batches.

1. `studio-op triage --json`. A flight paused with `phaseReason`
   `integration-branch-red`, or members whose `nextOperation.reason` is
   `integration-branch-red`, means the durable base verdict for that base SHA
   is red. The members are not at fault.
2. Do not send rounds to the members and do not eject them to get around the
   pause: the base stays red for every gate.
3. When the base verdict is provisional (a collided or loaded run), Studio runs
   the base again on its own. Wait one planning cycle and read triage again.
4. When it stays red, create one repair card for the base with the failing
   test names from the batch report:
   `studio-op create --ready --project <project> --title "Repair red integration branch: <test>" --prompt-file <file>`.
   When the failing test is a known flaky test, the card quarantines it: it
   adds an entry with the test name, the owning card and an `expiresOn` date to
   the project's `.agent-studio/test-quarantine.json`. The gate then ignores
   that test's failures until the expiry and still names them in its report.
   Otherwise the card fixes the base. Either way the change is a card; you never
   edit the repository or the quarantine file yourself.
5. The held batches reconstruct once the base SHA moves. Read triage again
   after the repair card integrates.
6. `studio-op log append --kind decided --project <project> --text "Base <sha> red; batches held; repair card <key>." --card <key>`

### 3. Stuck batch flight

A batch flight does not move, or a red batch isolates for a long time.

1. `studio-op triage --json`. For each member read `nextOperation`:
   `eject` (isolation active), `wait` with its `reason`, or `close-now`
   (the planner waits for size or deadline).
2. During isolation, eject is the only member action:
   `studio-op eject <card> --project <project>`. Eject a member only when its
   own delivery is the reason it cannot finish (for example a card the Owner
   withdrew), not to hurry the batch.
3. A member proven red returns to its agent with the gate evidence on its own;
   do not continue it while isolation still holds it (exit 11).
4. `studio-op` has no close-now command: a forming batch closes on its own
   size or deadline. When triage proposes `close-now` and waiting costs more
   than it saves, log the proposal for the Owner.
5. `studio-op log append --kind did --project <project> --text "Ejected <card> from flight <batch>: <reason>." --card <card>`

### 4. Lost pending push

A gated merge waits to reach origin, and nothing else integrates meanwhile.

1. `studio-op triage --json`. `pendingPushes[]` lists `jobId`, `approvedSha`,
   `ageMinutes` and `overdue` (older than `BatchGate:PendingPushAlertMinutes`).
2. Do not restart or deploy while a push is pending:
   `studio-op preflight restart` names it as `pending-push`.
3. The pending marker is a durable recovery job: Studio re-drives the push on
   each planning tick and at start, and clears markers already on origin,
   superseded or rolled back. Wait one more tick and read triage again.
4. When it stays overdue, log what you see and hand it to the Owner; a push to
   origin is a platform-owned Git step, never yours.
5. `studio-op log append --kind handoff --project <project> --text "Push of <job> (<sha>) pending <n> min; restart blocked." --card <job>`

## Other routine operations

- Continue a card: write the prompt to a file, then
  `studio-op continue <card> --prompt-file <file> --reason "<one sentence>"`.
  Modes: `--mode continue|steer|extend`.
- Lane moves: `studio-op move <card> <lane>`; requeue lanes refuse members
  under isolation.
- Title repair: `studio-op title <card> "<new title>"`.
- Capacity: `studio-op capacity` (quota per CLI, fallbacks, waits, pins).
- Backups: `studio-op backups` (last good backup, age, restore verification,
  off-host copy, warnings). A `backup-stale`, `backup-failed`,
  `verification-failed` or `offhost-failed` warning goes to the Owner with the
  message; the backup target and keys stay with the Owner
  ([runbook](../../../docs/operations/setup/studio-backup-restore.md)).
- Security: `studio-op security` lists the installation's security entries
  ranked by severity (expiring tokens and sign-ins, users without a second
  factor, hosts or CLIs behind, audit anomalies, open security cards);
  `--soon` keeps what needs action within 14 days. It only reads: report an
  entry to the Owner, because credentials, second factors and the kill switch
  stay with the Owner.
- Review step models: `studio-op routing` shows the central model selection
  policy and pins; `studio-op routing set-step --step <step id> --cli <cli> --model <model> --all`
  changes one step for every readable project through its model selection step
  override, keeping the step's other settings. The central consumer policy is
  an installation-wide setting and stays with the Owner.

## Anti-patterns

- Polling `/api/tasks/grouped` in a loop instead of `studio-op watch`.
- Running two watchers, or killing another session's watcher.
- Accepting a card because "the review is probably fine".
- Putting a token, a password or a recovery code into any text.
- Using SSH, `sudo`, or a root shell to repair Git state in project data.
- Retrying a 409 or 412 without re-reading the card.
