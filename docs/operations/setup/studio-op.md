# studio-op: the operator CLI

`studio-op` is the official command-line client for operating Studio. A
coding-agent CLI session (Claude Code or Codex) and the Owner in a plain
terminal use it the same way. It calls the documented operator routes
([operator API reference](../operator-api.md)) and adds no route of its own.
The token file, output redaction, the one-watcher lock and the acceptance
check live in its code, not in prompt text. Decision record: the
[operator-agent-level Dossier](../../operator-agent-level/index.html), OA-D1
option B (official CLI over documented REST plus one portable skill) and OA-D5
(accept only with green evidence and no open decision).

The portable [operator skill](../../../.agents/skills/operator/SKILL.md)
teaches the runbooks in terms of these commands.

## Install

`studio-op` is a Node script with no dependencies beyond Node 22, the
repository toolchain (`.nvmrc`). It lives in `tools/studio-op/`.

Install it for the OS user that runs the operator agent (see
[operator agent setup](operator-agent.md), step 2: its own user, without your
SSH keys):

```sh
# From a checkout of this repository, as the agent user:
npm install --global ./tools/studio-op
studio-op --version
```

Without a global install, run `node tools/studio-op/studio-op.mjs` in
place of `studio-op`.

To give an agent session the runbooks, make the skill folder
`.agents/skills/operator/` visible to it: sessions started in this repository
find it through `AGENTS.md`; for other working directories, copy or link the
folder into the CLI's personal skills folder (for Claude Code
`~/.claude/skills/operator/`, for Codex `~/.codex/skills/operator/`).

## Set up the token (Owner)

The token is read from exactly one file. There is no environment variable, no
command-line option and no fallback to another file.

1. Create the credential in Studio, signed in as the Owner:
   - **API token** (available today): **Settings > Access**, section **Service accounts and API tokens**, role
     `operator` (it binds the narrow `operator-agent` role, OA-D4), the
     projects to operate or all projects, expiry 30 days (the default, at most
     365). Studio shows the secret once.
   - **Operator session** (security model SM-1): **Settings > Access >
     Operator agents > Start session**, at most 8 hours.
2. As the agent user, write the secret into the file, never into a shell
   argument or a variable:

   ```sh
   umask 077
   mkdir -p ~/.config/agent-studio
   cat > ~/.config/agent-studio/operator-token   # paste, press Enter, then Ctrl-D
   chmod 600 ~/.config/agent-studio/operator-token
   ```

   For an operator session use the file name `operator-session` instead.
3. Point `studio-op` at the installation in `~/.config/agent-studio/studio-op.json`:

   ```json
   { "url": "https://studio.example.test" }
   ```

   Add `"credential": "operator-session"` to read the session file instead of
   `operator-token`. Allowed keys are `url`, `credential` and `clientId`; the
   file never holds a token. `--url` or `STUDIO_OP_URL` override the URL for one
   call. Without any URL, `studio-op` uses the local stable backend
   `http://127.0.0.1:5031`.
4. Check it:

   ```sh
   studio-op auth status
   ```

   It prints the file, its mode, the token kind and id (never the secret), the
   identity check against Studio, the readable projects, the routes an operator
   credential never reaches, and what stays with the Owner.

Rotate by writing the new secret into the same file; revoke the old token in
Settings > Access. If a secret was exposed anywhere, revoke it at once.

### Refusals

`studio-op` refuses before any request leaves the machine, with exit code 3:

| Code | Meaning |
|---|---|
| `token-file-missing` | An HTTPS or non-loopback Studio and no token file, or the configured credential file does not exist. |
| `token-file-permissions` | Group or others may access the file. Run `chmod 600` on it. |
| `token-file-owner`, `token-file-not-regular` | The file belongs to another user, or is a link or directory. |
| `token-file-empty`, `token-file-invalid` | The file is empty or holds more than one value. |
| `token-environment-refused` | `AGENT_STUDIO_API_TOKEN` is set, or any environment variable holds a token-shaped value. |
| `token-argument-refused` | A `--token` option or a token-shaped argument was given. |
| `token-requires-https` | A token would travel over plain HTTP to a non-loopback host. |

A prompt, title or log text that contains a token-shaped value is refused with
`token-in-content` (exit 2). Every line `studio-op` writes, including Studio
answers it prints, passes through redaction of the loaded token, every
Studio-issued token shape (`api.`, `sat.`, `oas.` and the other prefixes) and
bearer header values.

## Commands

Every command accepts `--json` (one JSON document on stdout; progress notes go
to stderr) and `--url`.

| Command | Routes | Notes |
|---|---|---|
| `auth status` | `GET /api/operator/decisions?limit=1`, `GET /api/watch-paths` | Local file checks plus the identity probe. |
| `watch [--session <label>] [--after <cursor>\|--from-start] [--take-over --reason <text>] [--timeout <s>]` | `POST/DELETE /api/operator/watcher/lease`, `.../heartbeat`, `GET /api/operator/decisions/stream` | See [Watch](#watch). |
| `triage [--project <p>]` | `GET /api/operator/batch-triage` | Flights, red and isolating members, pending pushes with age, next safe member operation. |
| `eject <card> [--project <p>]` | `POST /api/projects/{p}/batch-gate/members/{card}/eject` | The only member action during isolation. |
| `continue <card> --prompt-file <f> [--mode continue\|steer\|extend] [--reason] [--base-ref] [--model] [--cli] [--thinking]` | `POST /api/tasks/{card}/attention`, `POST /api/tasks/{card}/continue` | Studio starts from the card's latest result ref and returns it as `base`. |
| `move <card> <lane> [--run-intent revoke\|steer]` | `.../attention`, `POST /api/tasks/{card}/move` | Refuses the accepted lanes with `use-accept`. |
| `accept <card> [--reason <text>]` | `GET /api/tasks/{card}`, `GET .../review-projection`, `.../attention`, `.../move`, `POST /api/operator/log` | See [Accept](#accept). |
| `create --project <p> --title <t> [--ready] [--prompt-file <f>] [--type] [--cli]` | `GET /api/watch-paths`, `POST /api/tasks/` | `--ready` creates in `2-ready`, otherwise `0-backlog`. |
| `title <card> <title>` | `.../attention`, `PUT /api/tasks/{card}/title` | |
| `capacity` | `GET /api/operator/capacity` | |
| `backups` | `GET /api/operator/backups` | Installation backup state and warnings (AGT-3574); the off-host target and key fingerprints are shown only with installation-wide operate permission, and host paths in error text read `<path>` without it (AGT-3737). |
| `security [--soon]` | `GET /api/security/posture` | Read only. The installation security view ranked by severity; `--soon` lists only what needs action within 14 days, in the text and the `--json` findings (`soonOnly: true`; the summary keeps the installation-wide counts). Needs `security.posture.read`, which the `operator-agent` role holds. |
| `routing` | `GET /api/model-selection/policy`, `GET /api/operator/capacity` | Central model selection policy and pins. |
| `routing set-step --step <id> --cli <cli> [--model] [--thinking] [--pin-mode] [--pipeline-type] (--project <p>... \| --all)` | `GET /api/projects/settings`, `PUT /api/projects/{p}/pipeline-step` | See [Routing](#routing). |
| `preflight restart\|deploy` | `GET /api/operator/preflight/{operation}` | Exit 8 with the named blockers. |
| `deploy plan --target-version <v> [--window <minutes>]` | `POST /api/operator/deploy/plan` | Ordered, expiring plan: drain, Task Server, readiness, Engine, hosts. |
| `log [--project <p>] [--after <cursor>]` | `GET /api/operator/log` | |
| `log append --project <p> --kind observed\|decided\|did\|handoff (--text <t> \| --text-file <f>) [--card <key>]... [--audit <id>]...` | `POST /api/operator/log` | |

For a networked installation, `studio-op deploy plan` supplies the OP-6 plan;
the [networked deploy runbook](networked-deploy.md) drives the complete host
release. From the operator checkout, run
`bash deploy/networked/deploy.sh deploy <commit> --manifest <private-manifest> --journal <private-journal> --dry-run`,
then repeat without `--dry-run` to deploy. To restore the captured versions,
run `bash deploy/networked/deploy.sh rollback <commit> --manifest <private-manifest> --journal <private-journal>`.
Keep the same journal and manifest for rollback.

Card writes (`continue`, `move`, `accept`, `title`) first take or renew the
card's attention lease and send its revision as `If-Match`, as the
[attention contract](../operator-api.md#operator-log-and-card-attention)
requires for operator tokens. A 409 `attention-held` or 412
`stale-card-revision` means another actor works on the card.

### Watch

`studio-op watch` replaces board polling. It:

1. creates the local lock `~/.config/agent-studio/studio-op-watch.lock`
   (mode 600, holding the process id and lease id); a second watch on the same
   machine exits 9 with `scope: local-lock` without a request, and a lock left
   by a dead process is replaced;
2. takes the installation's watcher lease; a watcher on another machine gets
   409 `watcher-active` and exits 9 with `scope: server-lease`, the holder, its
   session label and its last heartbeat;
3. renews the lease at the heartbeat interval Studio returns, and exits 28 when
   the lease is lost;
4. reads the decision stream from the cursor saved by the previous watch in
   `studio-op-watch.cursor.json` (or `--after`, `--from-start`, or from now on
   the first run), reconnecting with the last cursor after an interruption;
5. prints the first actionable event and exits with its class code. A
   `watcher.heartbeat` event is not actionable. The lease and the lock are
   released on every exit.

`--take-over --reason <text>` replaces an active holder; Studio audits it in
the control plane audit before the lease changes.

### Accept

`studio-op accept` runs the OA-D5 check on the card and its fresh review
projection and refuses with exit 7 and every reason:

| Reason | When |
|---|---|
| `not-in-human-review` | The card is not in `5-human-review`. |
| `decision-card` | Decision cards stay with the Owner. |
| `decision-open` | A park for a human decision, an agent question or an unanswered steer (the blocker types the acceptance rail holds), an escalation, an `escalate` or `pending` orchestrator verdict, or an unanswered steer. |
| `evidence-missing` | No review attempt, or build and test evidence not proven. |
| `evidence-red` | The latest review is not `Pass`, build or tests failed, an aspect blocks, the delivery gate failed, or the verdict is reissue. |
| `evidence-disputed` | The latest local review reports concerns. |

When the evidence is green it moves the card to `6-completed` (Studio still
requires integration proof for that move) and appends a `did` entry to the
operator log that records the acceptance as the operator agent's verdict.
The card timeline names the token's own actor, not the Owner.

### Routing

The Dossier routes review model changes through central model selection
(AGT-3459). The central consumer policy (`PUT
/api/model-selection/policy/consumers/{consumer}`) is an installation-wide
setting and stays with the Owner. An operator credential changes the per-step
layer of model selection: `routing set-step` writes the step's model selection
override for each project in scope with `PUT /api/projects/{p}/pipeline-step`
(`project.pipeline.write`), in one command instead of a loop. The route
replaces the whole step setting, so the command reads the current setting
first and keeps its other fields (enabled, mode, prompt, condition). It reports
one result per project; exit 6 means some projects refused, exit 1 all.

## Exit codes

| Code | Meaning |
|---:|---|
| 0 | Done. `watch` with `--timeout`: no event in time. |
| 1 | Failure (Studio 5xx or an unexpected error). |
| 2 | Usage error, invalid input, `use-accept`, `token-in-content`; Studio 400. |
| 3 | Credential refusal (table above) or Studio 401. |
| 4 | Forbidden (Studio 403). |
| 5 | Not found (Studio 404). |
| 6 | Conflict: Studio 409, 412 or 428 without a more specific code. |
| 7 | `accept-refused`: evidence not green or a decision open. |
| 8 | `preflight-blocked`: the restart or deploy preflight names blockers. |
| 9 | `watcher-active`: another watcher holds the lock or lease. |
| 10 | `studio-unreachable`. |
| 11 | `batch-isolation-active`: the card is a member of a batch under isolation. |
| 12 | `approval-required`: a T2 operation waits for the Owner's approval. |
| 20 | Watch woke on `escalation.opened`. |
| 21 | Watch woke on `human-review.ready`. |
| 22 | Watch woke on `decision-card.opened`. |
| 23 | Watch woke on `backend.stalled` or `progress.idle-with-ready`. |
| 24 | Watch woke on `batch.red` or `batch.isolating`. |
| 25 | Watch woke on `push.pending-overdue`. |
| 26 | Watch woke on `quota.exhausted` or `quota.fallback-active`. |
| 27 | Watch woke on `stream.gap`: events were lost; read triage and capacity. |
| 28 | The watcher lease was lost (taken over). |
| 29 | Watch woke on an event type this release does not know. |
| 130 | Watch was interrupted (SIGINT or SIGTERM). |

The numbers are stable; new codes take new numbers.

## Not included

`studio-op` has no Git command, no SSH, and no host execution: a deploy plan is
printed, and its host steps run as typed host operations the Owner granted. An
MCP server is out of scope; one may wrap this CLI later.

## Verification

`node --test tools/studio-op/test/*.test.mjs` runs the unit and fake-Studio
tests (`npm --prefix frontend run lint:studio-op` in the lint chain). The
end-to-end spec `frontend/e2e/operator/studio-op-operator-cli.spec.ts` runs
every working-set operation against the prebuilt networked stack in its own
`operator-cli` profile: `npm --prefix frontend run e2e -- e2e/operator/studio-op-operator-cli.spec.ts`.
