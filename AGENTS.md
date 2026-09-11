# AGENTS.md

> Root navigation card for Codex CLI, Claude Code, GitHub Copilot coding agent,
> and future agent surfaces. Keep this file short. Mandatory guardrails live
> here; domain system-of-record maps live in `docs/`.

Compatibility shims: [CLAUDE.md](CLAUDE.md), [GEMINI.md](GEMINI.md), and
[.github/copilot-instructions.md](.github/copilot-instructions.md).
Frontend-scoped rules live in [frontend/AGENTS.md](frontend/AGENTS.md) and
apply only under `frontend/`.

## Start Here

- Product overview: [README.md](README.md).
- Future-only product direction: [ROADMAP.md](ROADMAP.md).
- Canonical contribution and style conventions for all agents:
  [docs/start/contribution-and-style-guide.html](docs/start/contribution-and-style-guide.html).
- Documentation index: [docs/start/README.md](docs/start/README.md). Start there when you
  do not already know the right document.
- Operator setup and troubleshooting: [docs/operations/setup/](./docs/operations/setup/README.md).
- Familiar runtime, CLI, permission, filesystem, runner, or state-machine
  failure: search the thematic common-problems libraries
  ([docs/operations/common-problems/](docs/operations/common-problems/) and
  [docs/system/common-problems/](docs/system/common-problems/))
  before debugging from scratch.
- Current state of a designated topic (so we do not re-discover the same ground):
  [docs/concepts/designated-topics/](docs/concepts/designated-topics/README.md).
  The opt-in `post-agents-wiki-sync` step keeps these pointers consistent and the
  per-topic Current State pages fresh.

## Non-Negotiable Rules

- UI work obeys the style-guide hard rules in
  [docs/quality/design/style-guide-hard-rules.md](./docs/quality/design/style-guide-hard-rules.md).
  Most-cited: no coloured left accent line or bar on cards, panels, rows,
  banners, or pill groups (encode status via background tint, badge, or dot).
- New or touched admin UI also obeys the flat-surface contract and reference
  mockup in
  [docs/operations/admin-design-guideline/index.html](./docs/operations/admin-design-guideline/index.html).
  Apply it incrementally; do not turn a scoped card into a broad restyling pass.
- Work only in the active dev checkout or assigned task worktree. Never edit
  `agent-taskboard-stable/`; stable updates only by the parent
  `update-stable.sh` after a verified dev batch.
- Never directly create, move, edit, delete, or rename anything under
  `agent-taskboard-workspace/projects/**` or
  `agent-taskboard-workspace/.metadata/**`. Use the application API. If the API
  lacks the operation, queue a task instead of bypassing it.
- For task creation, movement, reissue, archive, or triage, read the Task API
  skill first: [.agents/skills/task-api/SKILL.md](.agents/skills/task-api/SKILL.md).
- Do not commit, push, amend, or mutate remotes unless this exact interactive
  task asks for it. Managed task runs leave git ownership to the platform; see
  [docs/operations/git/commit-push-doctrine.md](./docs/operations/git/commit-push-doctrine.md).
- Platform-owned commits are remote-durability boundaries: push them
  immediately by default in every managed repository. Keep network work off
  the run path, retry failures with bounded backoff, and surface final failures
  as typed operator-feed events rather than treating a local commit as durable.
- Follow the canonical contribution and style guide linked above. Its language
  policy applies to UI strings, backend errors shown to the UI, prompts,
  comments, docs, commits, and PR text. Do not introduce em dashes.
- When adding a document under `docs/`, add one row to
  [docs/start/README.md](docs/start/README.md) in the same change.
- CLI crashes, run-outcome classification, retries, or orchestrator
  drive-to-conclusion: read
  [docs/concepts/orchestrator-drive-to-conclusion.html](docs/concepts/orchestrator-drive-to-conclusion.html)
  before changing that logic, and maintain it after. Append each incident to
  its case log (date, slug, what crashed, which terminal it reached).
- A repeatedly busy orphan worktree is a bounded drive-to-conclusion failure:
  after five preparation attempts, escalate it with a visible
  `worktree-blocked` gate item containing the busy path, and pause retries.
  Repeated `pick-reverted-no-run` notices remain rate-limited per task.
- Agent shell policy: default to bash/sh, prefer existing `.sh` scripts, and
  avoid PowerShell-specific file creation.

## Domain Maps

| Area | Read first | Owns |
|---|---|---|
| Runner | [docs/system/domains/runner.md](./docs/system/domains/runner.md) | Pickup, CLI run loop, outcome policy, supervisor loops, and the standalone remote runner (`runner/`, [runbook](./docs/operations/setup/linux-runner-host.md)). |
| Pipeline | [docs/system/domains/pipeline.md](./docs/system/domains/pipeline.md) | Pre/core/post steps, pipeline history, step contracts, and the review/aspect evidence contract (branch diff + `results/` inventory + card mode; when "deliverables missing" is legitimate). |
| Review | [docs/system/domains/review.md](./docs/system/domains/review.md) | Remote Review material, merge-base diff budgets, semantic verdict citations, grading, and acceptance-rail behavior. |
| Model routing | [docs/system/domains/model-routing-policy.md](./docs/system/domains/model-routing-policy.md) | Canonical model and thinking-level tiers, weighted selection, correctness floors, benchmark evidence, and quota handling. |
| Tasks | [docs/system/domains/tasks.md](./docs/system/domains/tasks.md) | Job folders, lane states, API mutations, task access, Epic lifecycle and archive-inclusive history, and the project onboarding and central-store contract. |
| Frontend | [docs/system/domains/frontend.md](./docs/system/domains/frontend.md) | Angular surfaces, design system, Playwright proof. |
| Style guides | [docs/quality/README.md](./docs/quality/README.md) | Technology-aware Angular and .NET guidance selected into coding prompts; incorporates the hard design baseline. |
| Design rules | [docs/quality/design/style-guide-hard-rules.md](./docs/quality/design/style-guide-hard-rules.md) | Hard, non-negotiable design rules (no left accent bars, full-bleed views, aggregate = sum of visible children, acute-only signals, both themes). |
| CLI | [docs/system/domains/cli.md](./docs/system/domains/cli.md) | Claude, Codex, Copilot, Gemini drivers and quota probes. |
| ADRs | [docs/system/architecture/decisions/adr-archive.md](./docs/system/architecture/decisions/adr-archive.md) | Load-bearing decisions and deliberate non-goals. |
| Skills | [.agents/skills/README.md](.agents/skills/README.md) | Portable specialist workflows. |

## Product Boundaries

- Default task execution is sequential within one project.
- Parallelism across projects is in scope.
- Opt-in intra-project parallelism exists only through orchestrator-gated
  `maxParallelism`, isolated worktrees, and pipeline-owned git steps
  ([ADR-0052](docs/concepts/parallel-task-execution.md)).
- Do not add workflow engines, per-task workspaces, unbounded fan-out, or a
  design where the run agent manages git.
- The Companion App is reference code only. Do not extend it or advertise it as
  current product capability.

## Runtime And Stable

- Dev backend port 5030 is offline by default. Agents may bring it up only via a
  Playwright spec from stable that uses
  [frontend/e2e/fixtures/dev-backend.ts](frontend/e2e/fixtures/dev-backend.ts).
- Do not start dev backend from a supervisor session, auto-mode loop, background
  watcher, or routine shell command.
- Stable is the supervisor seat. Never update stable mid-run. After a stable
  restart, verify runner mode and retry the resume path until the intended mode
  is visible.
- Backend lifecycle scripts are shell scripts. Prefer repo scripts such as
  [api.sh](api.sh) over ad-hoc process control.

## Verification

- Regression reports need data first: reproduce, measure, find the test gap,
  write the failing regression test, fix, then repeat the original measurement.
- Prompt changes under `prompts/runtime/` are CLI behavior changes. Run the
  matching live probe, such as the `@billable` hello-world spec, or state why it
  could not be run.
- Visual or behavioral frontend changes require relevant Playwright coverage and
  screenshots. Persist review screenshots under the task `results/` folder when
  they must survive `test-results/` cleanup.
- Pure documentation-only changes do not need builds or Playwright. Verify the
  navigation, link targets, and diff.

## Finish Criteria

- Keep changes scoped to the task.
- Update docs and ADRs only when the change affects product direction, public
  behavior, architecture, CLI contracts, filesystem contracts, or agent workflow.
- ADRs are for load-bearing decisions and deliberate non-goals, not changelog
  entries or bug-fix notes.
- Report changed files and verification. If no documentation update is needed
  for a code change, say so briefly.
