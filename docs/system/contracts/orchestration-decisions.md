# Orchestration decision library

AGT-3057 implements AGT-W65 D14 A as an extraction slice, with D17 A and D18 A.
`orchestration-decisions/Orchestration.Decisions.csproj` owns the deterministic
transition policies. It references only TaskServer.Contracts and the .NET
runtime. It reads no files, configuration, clock, processes, or HTTP services.
Every library type uses `AgentStudio.OrchestrationDecisions`. State, timeline,
failure-domain, and outcome constants are shared from TaskServer.Contracts,
so the hosts and library read the same persisted tokens. Verdicts classified
`BlockWithoutCitation` do not open a finding round or count as a repeated block.

The monolith still invokes these policies by default. AGT-3058 (I16) adds a
file-backed orchestration occurrence journal and the v1 claim, lease and stage
completion routes on the monolith authority. The engine can decide configured
coding completions, review reports, gate results and operator sweeps while the
card remains in its current lane with a visible wait marker. The stage receipt
is applied through the existing keyed lane writer, prompt and continuation
stores, integration coordinator, batch queue and escalation path. I17 runs the
dedicated engine remotely; I18 moves the remaining hosted scheduling. This
library does not start an engine process or change deployment roles.

`Orchestration:Transitions:CodingCompletion`, `ReviewReport`, `GateResult`, and
`OperatorSweep` each accept `Monolith` (default) or `Engine`. The monolith
answers protocol negotiation with the `engine` client kind and the
`orchestration-plane` capability. A separately enrolled `engine_` principal
uses the exclusive `orchestration.write` credential scope. The occurrence
journal lives under `<TaskRepository>/.orchestration-occurrences`; it preserves
pending work, lease fences and completed stage receipts across restarts.
Review-report occurrences are prepared before authority settlement but become
claimable only after an accepted report. Recovery restores wait markers only for
claimable occurrences. An unaccepted preparation has no wait marker and, after
a bounded grace period, receives a durable cancellation receipt. A later valid
report can prepare the same idempotency key again.

AGT-3059 adds a per-profile rollout journal at
`<TaskRepository>/.orchestration-rollout.json`. Coding completion, review report,
and gate or batch result can be switched independently from Monolith to Shadow and then
to Engine. Shadow records both receipts for each occurrence and compares chosen
actions and input digests. A mismatch resets the matching streak and records
both actions and reasons. The default threshold is 50 consecutive matches;
`Orchestration:ShadowMatchThreshold` can change it. Ready is visible but does
not switch automatically. A Studio operator switches or rolls back through one
API call with an actor and reason receipt. Rollback keeps pending occurrences
and resumes them through the monolith's shared library. Unactivated review
occurrences do not carry a waiting marker. Engine mode never invokes the
monolith decision on engine unavailability; after one minute a pending
occurrence raises an operator feed alert and waits for a leased engine instance.

## Decision grounds, shadow receipts and active coding choice

AGT-3063 implements AGT-W73 OD-S1 for the `CodingCompletion` profile while it
is in Shadow. When the monolith records the shadow occurrence,
`DecisionGroundsCollector` freezes a `DecisionShadowInputV1` under
`<TaskRepository>/.orchestrator-decisions/inputs`. It holds typed facts with
source refs, freshness, coverage and reliability, explicit omissions, the
candidate actions with floor-rejected ones visible, the last decisions with
their applied outcome, the model route, and the AGT-W68 RB-S1 card ledger and
progress definition. The deterministic choice sits beside the grounds and stays
out of the model prompt. The first frozen input of an occurrence wins.

The library owns the pure rules. `CardRunLedgerPolicy` deduplicates usage by
provider call id (falling back to a run, time, model and counter fingerprint),
subtracts cached input from a raw OpenAI total before pricing, keeps unknown
prices unpriced, and reports excluded duplicate rows, tokens and value.
`DecisionGroundsPolicy.Freeze` builds `DecisionGroundsV1` with fixed row caps,
truncation markers, at most 32 KiB, a 6,500-token pack budget that leaves room
for the prompt inside the 8,000-token call cap, an incomplete-pack flag, and a
SHA-256 content digest. `DecisionReceiptPolicy` renders the prompt, accepts only
an available candidate from the closed union with valid confidence and cited
fact ids, and builds `OrchestratorDecisionReceiptV1`. Any other answer, an
unavailable model, an expired wait, a cap or an incomplete pack records
`ask(reason=decision-unavailable)`.

The engine's `ShadowDecisionService` drains occurrences without a receipt. By
default the model call is a claimable decision job on a runner host
(`runner-host-job` custody), so provider credentials stay on runner hosts. A
runner claims it at `/api/runner/orchestrator-decisions/claim` with the
`runner.claim` scope and runs one read-only CLI turn in an empty directory. A
transport failure gets one attempt on `Orchestration:DecisionModel:Alternate*`.
The monolith prices the deduplicated usage with `ITokenPricing` at the call
time. `DecisionCostCapPolicy` holds the cost protection. A card occurrence is
refused beyond 24 calls or 5 USD priced spend per card. An occurrence without a
card (the Fluid capacity periodic host check) is charged to its host-day
instead: 48 scheduled plus 12 event calls, 180,000 input and 90,000 output
tokens and 10 USD priced spend per host and UTC day, where open jobs reserve
their envelope. A refused job stays `cap-exhausted`. Each call carries an
envelope of at most 8,000 input and 1,000 output tokens. The runner refuses a
prompt whose estimate exceeds it before spawning, and passes the output cap to
the CLI (`CLAUDE_CODE_MAX_OUTPUT_TOKENS`, Codex `model_max_output_tokens`). The
store rejects a completion whose measured usage broke the envelope. It keeps
the usage and price but does not accept the answer, so the receipt records
`ask(reason=decision-unavailable)`. Receipts are immutable: the store checks the grounds
digest against the frozen input and returns the first receipt on every replay.
Storing a receipt also closes any job that is still open. A billed completion
that arrives after that (for example after a timeout receipt) still records
its usage and price on the job. The card spend counts it, and the receipt row
and its outcome link add it as a late call (`lateCalls`). Studio reads
`/api/orchestration/decision-receipts`; each row's `evidenceRef` opens the full
receipt with grounds and response, and `outcomeLink` shows the applied action.
In Shadow, the model choice is never dispatched. In Engine mode, the coding
completion profile freezes the same grounds before activating its occurrence.
The engine waits for the runner-hosted model job, records an active receipt,
then submits the chosen action through stage completion. The Task Server
validates the receipt against the frozen grounds, the current authority epoch
and fence, the settled run and exact result SHA/ref, and the offered action's
A0 or A1 floor before applying an effect. A valid `prepare(review-subject)`
creates review; `retry(card:ready)` requeues; `adapt(salvage-handoff)` pins the
immutable result ref as the next round's base; `checkpoint(verified-tree)`
writes `results/checkpoint.md` with the last verified SHA and holds the card.
Unusable output and unavailable or capped calls record `ask(operator:ai)`
with the call status and no-call reason, and leave the card in its source lane.
An A2/A3 proposal cannot mutate the card. The active profile can be disabled
for a compatibility rollback with
`Orchestration:DecisionModel:ActiveCodingCompletion=false`; the default in
Engine mode is active. The receipt's outcome link shows the later applied
stage action. `Orchestration:DecisionModel:Cli`, `Model`
and `ThinkingLevel` default to `codex`, `gpt-6-sol` and `medium`; the engine
reads `SHADOW_DECISION_CONCURRENCY` (2) and `SHADOW_DECISION_TIMEOUT_SECONDS`
(900). An engine-held provider credential remains a separate follow-up decision
if claim latency proves too high.

## Inputs and outputs

`TransitionFacts` contains card identity and lane, attempt identity and settled
envelope evidence, reviewed ref and SHA, verdict findings and frozen plan,
project settings, lifetime automatic-round and concern counters, delivered-round
history, infrastructure retry availability, and gate/batch state.

`TransitionDecision` returns an action, reason, receipt, constrained candidates,
findings, and the continuation base. Receipts identify the card, attempt, rule,
rounds used and allowed, and affected aspects. Candidate authority floors remain
mandatory if a later AGT-W73 model step proposes a different action. The current
library returns the deterministic candidate only; it does not call a model.

| Action | AGT-W73 union | Authority or required effect |
|---|---|---|
| CreateReview | prepare | Mint a review subject for a verified coding delivery |
| FindingRound | adapt | Carry findings and continue on the reviewed ref and SHA |
| ConcernRound | adapt | Carry actionable concerns within both applicable budgets |
| AcceptAndIntegrate | continue | Settled immutable envelope and admissible build/test gate |
| DeferToBatch | defer | Batch owns full verification and publication |
| HumanReview | ask | Operator disposition |
| Escalate | ask | Operator disposition; never a further automatic test-block round |
| ReviewInfrastructureRetry | retry | Remaining retry budget and no intervention/probe blocker |
| DegradeWithFollowUp | continue | Create and link the follow-up before completing the disposition |
| SweepIgnore | halt | No applicable trigger |
| SweepHold | defer | Active attempt, pause, already-handled subject, or environment owner |
| SweepRound | adapt | Eligible fix, gate-triage, or salvage trigger and available card budget |
| SweepWaitForPerson | ask | Unclassified gate failure or exhausted sweep budget |
| SalvageContinuation | adapt | Published salvage and remaining continuation budget |
| ReturnToReady | retry | Stopped, environmental, or mechanical-fallback completion |
| Continue | continue | Continue the existing decision chain |
| Halt | halt | Already-applied review or superseded card state |

## Ownership and compatibility

The library owns review follow-up selection, review budget composition, lifetime
card-budget decisions and counting, delivery integration admission, Council
reaction, reissue loop breaks, timeout salvage continuation, review continuation
replay, operator-sweep guards, infrastructure-repeat diagnosis, and standalone
stage and settlement policy. Policy names and typed action contracts remain
stable; hosts import the common namespace. Stores, clocks, timeline reads, prompt
rendering, Git, and lane writes stay with their application owners.

`MonolithTransitionAdapter`, `TaskServerTransitionAdapter`, and
`EngineTransitionAdapter` expose the common facts-to-decision seam. The remote
review endpoint and local review loop share `ReviewBudgetPolicy`. The Task
Server projects the reviewed result ref and frozen review plan into its engine
payload. Its transactional settlement applies linked follow-up creation and
reviewed-base persistence before the terminal lane write. The shared D18
decision sets the linked follow-up lane to Ready for every adapter. Stage
receipts carry the full typed decision; the existing stage protocol still uses its five wire
actions.

I11 (AGT-3061) completes the standalone loop. The Task Server applies the
engine's typed decision from the stage output after checking that it names the
card, matches the stage action, and continues on the reviewed result of the
run. Only an engine without a typed decision falls back to the same library
decision derived from the frozen payload; the settlement audit names the
decider. A fix round writes the D17 base with its `review-fix-round` reason and
one ordered rework prompt (`ReviewFindingRoundPrompt`, shared with the
monolith). Under the `reviewed-publication` delivery policy,
`OrchestrationSettlementPolicy` holds an accepted delivery in Auto Review as an
`integrating` run. `IntegrationResultPolicy` maps the integration report to
Human Review, a bounded stale-base retry, or escalation.

The monolith retains the existing delivered-round and recurrence limits as well
as the lifetime automatic-round cap. Extraction does not replenish a counter or
change the existing order of effects. Test blocks remain non-degradable, and an
unactionable concern does not spend an automatic round. Batch planning and
advisor policies remain in the AGT-3052 contracts package.

For an engine-owned review, the accepted report records its delivered round
before the prepared occurrence becomes claimable. Recovery repeats that keyed
write before activation if settlement succeeded but the endpoint stopped. A
cancelled preparation remains visible to activation recovery, and a rejected
report may reopen it with a corrected payload and renewed authority fence.
Gate decisions carry infrastructure and intervention facts to the shared
policy, which escalates those failures. Shadow comparison uses the action the
monolith actually applied after the gate handler completes. A batch
shadow decision is saved as an intent before the lane move and becomes the
receipt only after the move succeeded; a failed move withdraws it. Each
comparison counts an occurrence once; a receipt replay and the recovery service
compare every shadow occurrence that holds both receipts, so a comparison lost
between saving a receipt and comparing it still resets the matching streak.
The recovery service also resolves a shadow occurrence without a monolith
receipt from the card's current lane: a card in its source lane is still
deciding, a saved intent whose target lane the card reached is compared, and
any other occurrence cannot be compared and resets the streak. The switch to
Engine is refused with `orchestration-profile-shadow-unresolved` while an
earlier shadow occurrence of the profile lacks a receipt, so Ready never
hides an open comparison.

## Verification and architecture guard

`TransitionDecisionGoldenTests` runs the same review and sweep table through all
three adapters and compares actions and receipts, including D17 bases, D18
follow-up requirements, infrastructure failures, branch defects, and batch
routing. Endpoint, budget, lease, sweep, and engine-stage tests exercise the
application boundaries.

`TransitionDecisionBoundaryTests` is a `Category=Guard` architecture test in
backend.Tests. It pins the owning assembly, rejects host definitions of the
extracted policies and direct transition construction/branching patterns, and
forbids boundary access and ambient time in the library. Mutation examples prove
that the source guard rejects reintroduced rules. Host branches may inspect a
returned decision to apply it or validate persistence results. Add new transition
policies to the owned-policy inventory and put their facts and rules in this
library. Broader background-service movement remains I18.

Run the guard with:

```sh
dotnet test backend.Tests/OrchestratorApi.Tests.csproj --no-build --filter 'FullyQualifiedName~.Architecture.|Category=Guard'
```
