# Decision cards

A decision card is a single question that blocks concrete work. It has kind
`decision` and stays in `1-preparation` while pending. The board and list show
its Decision badge and decider, the project header counts open decisions, and
the wiki inbox lists them beside Dossier decisions. A Dossier can contain many
questions and may produce cards; it keeps its own lifecycle. Both hosts use the
same decision record format. The [approved concept](decision-cards/index.html)
records the alternatives, evidence, and operator choices D1 to D5.

## Lifecycle and fields

Create a card with a question and two to four structured options. Each option
has a stable `id`, `label`, optional `consequences`, `effort`, and `risk`, and
may name implementation `requirements`. A recommendation pairs an option id
with a reason. `decider` is a client identity or `role:<name>`; it defaults to
`operator`. `dueDate` is optional. `dependants` names work blocked by the
choice; `appliesTo` names existing implementation cards that receive it.
Pending decisions cannot enter a runner lane. Cards with a `references.dependsOn`
edge to a pending decision show `blocked by` and cannot be claimed or moved to
Ready or Progress. The decider chooses one option and supplies a rationale.
The card records the choice, actor, UTC time and wiki record path, then enters
the decided state. Reopening requires a note, retains the choice and reopen
history, and blocks dependants again.

An operator may decide the default `operator` role. A card assigned to a named
client identity or role accepts only that client or role. The decision API
records the caller identity from `X-Client-Id` and checks it against the
assigned decider. The recommendation is advice, not an automatic choice.

## Applying the choice

If `appliesTo` names an implementation card, the apply step appends a decision
block with the question, chosen option, rationale and decision link to its
prompt, then moves it to `2-ready`. If no implementation card is linked, the
chosen option's `requirements` create coding cards through concept promotion.
The decision history records the updated or created keys and the apply outcome.
An apply failure leaves the decision recorded and can be retried with the same
choice. The accepted AGT-2792 example has two options, lock file or no lock
file, and recommends A based on the feasibility probe. Choosing A with a
rationale produces a Ready implementation card that references AGT-2792 and
contains the lock-file work and shared release requirements.

## Reminders and API

An unanswered decision is due after three days by default if no due date was
set. The overdue sweep writes one reminder per pending cycle to the inbox and
activity feed and names the blocked cards. Reopening clears the reminder stamp
so a new cycle can be reminded.

| Action | API |
|---|---|
| Read cards and details | `GET /api/tasks/grouped` and `GET /api/tasks/{id}` |
| Create | `POST /api/tasks` with `kind: "decision"` and a `decision` object |
| Decide | `POST /api/tasks/{id}/decision` with `optionId` and `rationale` |
| Reopen | `POST /api/tasks/{id}/decision/reopen` with `note` |
| Migrate active prose | `POST /api/tasks/{id}/decision/migrate` with `expectedPromptSha256` and `decision` |

Mutations carry `X-Client-Id`. The migration endpoint accepts only an ordinary
task in Preparation or Escalated with a recognizable prose request, a matching
prompt digest, and valid decision content. The one-off operator script
`node scripts/migrate-decision-cards.mjs` inventories AGT-2792 and AGT-2736 to
AGT-2738 in dry-run mode. Review its report before running with `--apply`.
It skips settled cards and AGT-2795, which remains the concept card. The live
inventory on 2026-10-07 found AGT-2792 archived with option A already recorded
in prose, and the other three completed with operator answers; no live card was
converted. A pre-decision copy of AGT-2792 is the migration acceptance fixture.
