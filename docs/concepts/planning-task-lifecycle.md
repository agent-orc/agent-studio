# Planning-task lifecycle: plan → spawn → accept

> Prompt-known workflow for planning-mode tasks. Read this before running a
> planning task or accepting one in review. It exists because plan-only cards
> kept slipping through approved without the work they proposed ever being
> created (AGT-1915, Dossier-Plan-Phase-2). AGT-2069 makes that trap visible
> and gates against it.

## What a planning task is

A task's **execution mode** (`mode` field in `task.json`, independent of the
`kind` task/decision taxonomy) is one of `coding` (default, writes source),
`planning`, or `research`. Planning and research are **read-only**: they analyse
the codebase and produce a report; they skip the git pre/post pipeline (no
worktree, no commit, no merge). See the
[historical planning and research proposal](./planning-research-task-kinds-2026-05.md)
for the mode's origin and [`TaskModes`](../../backend/Shared/Models/TaskModes.cs)
for the current carrier.

A **planning task** answers "what should we build next in this codebase?" Its
deliverable is a concept plus the concrete follow-up work it proposes. The
report is not the end state — the follow-up cards are.

Historical note: this workflow first surfaced as ASS-1490 ("Planning task /
Research Task Differenzierung"), archived without implementation on 2026-07-07.
Its intent lives on here; the card was referenced, not reactivated.

## The lifecycle

1. **Plan (read-only run).** The planning agent investigates and writes its
   concept to the run report. When it proposes concrete next work, it puts the
   next task's prompt under a stable `## Proposed task prompt` heading (see
   [`PlanningPromotion`](../../backend/Shared/Models/PlanningPromotion.cs)) so
   the promote flow can pre-fill a coding task from it.

2. **Concept in `results/`.** Durable artefacts (specs, design notes,
   screenshots) belong in the task's `results/` folder so they survive
   `test-results/` cleanup and are reviewable.

3. **Spawn follow-up cards.** The plan becomes work in one of two ways:
   - **Manual promote.** On a finished planning task the detail view offers
     **Promote to coding task**, which opens the create-task modal pre-filled
     from the `## Proposed task prompt` section (title, prompt body, and copied
     images). See `GET /api/tasks/{id}/promote-to-coding`.
   - **Automatic spawn.** The opt-in `post-task-spawner` pipeline step (AGT-2028,
     [`TaskSpawnerPostStepRunner`](../../backend/Features/Pipeline/TaskSpawnerPostStepRunner.cs))
     can create a follow-up card in a target project and records it in the
     source task's `.metadata/spawned-tasks.jsonl` ledger with a `relatedTo`
     back-reference.

   Either way the spawned cards are surfaced on the planning task's detail as
   "spawnt: AGT-xxxx" reference microcards (AGT-2050).

4. **Or declare "no follow-up intended".** Sometimes a plan deliberately
   concludes that no work should follow (concept archived, superseded,
   intentionally no code change). That is a legitimate outcome, but it must be an
   **explicit call**, not a silent slip. Record it from the planning task's
   detail ("Declare: no follow-up intended", optional reason), which writes the
   app-owned `.metadata/planning-closure.json` sidecar via
   `POST /api/tasks/{id}/planning-closure`.

5. **Accept.** Accepting a planning task into `6-completed` passes through the
   **spawn-contract gate**.

## The spawn contract (the AGT-1915 guard)

A planning task is "done" only when its spawn contract is satisfied:

> **spawned at least one follow-up card** OR **declared no follow-up intended**.

The pure decision lives in
[`PlanningCompletionGate`](../../backend/Shared/Models/PlanningSpawn.cs) and is
surfaced read-time as `TaskInfo.PlanningSpawn` (a `PlanningSpawnSummary` present
only on planning cards), so the same yes/no answer drives:

- the **spawn-visibility panel** on the planning task's detail (follow-up
  microcards, or a loud "No follow-up cards created" warning with the declare
  action);
- the **contract badge** ("contract met" / "no follow-ups");
- the **accept-dialog warning** — accepting an unsatisfied planning task pops a
  confirm dialog ("Planning task without follow-up cards — accept anyway?"), the
  exact 1915 trap made un-missable. The operator can still override, but never by
  accident.

The gate is a visible, confirmable guard, not a hard wall: a deliberate accept
is always possible, but a plan can no longer be quietly approved into nothing.

## Visibility at a glance

Planning (and research) tasks carry a prominent filled **mode badge** on the
board card and the task-detail header, so "here work is PLANNED" is obvious
before you open the card. Follow-up spawn state and the contract badge live on
the detail's Overview, next to the Promote affordance.

## Related

- Task modes taxonomy: [research/planning-research-task-kinds-2026-05.md](./planning-research-task-kinds-2026-05.md)
- Task-spawner step: [`TaskSpawnerPostStepRunner`](../../backend/Features/Pipeline/TaskSpawnerPostStepRunner.cs) (AGT-2028)
- Reference microcards: `app/components/task-reference-microcard` (AGT-2050)
- Tasks domain: [domains/tasks.md](../system/domains/tasks.md)
