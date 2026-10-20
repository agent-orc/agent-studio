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
both actions and reasons. The streak follows occurrence creation sequence,
including comparisons recovered after newer outcomes. The default threshold
is 50 consecutive matches;
`Orchestration:ShadowMatchThreshold` can change it. Ready is visible but does
not switch automatically. A Studio operator switches or rolls back through one
API call with an actor and reason receipt. Ready leads to Engine only through
the applier canary described in
[Applier contract and canary](#applier-contract-and-canary). Rollback keeps pending occurrences
and resumes them through the monolith's shared library. Unactivated review
occurrences do not carry a waiting marker. Engine mode never invokes the
monolith decision on engine unavailability; after one minute a pending
occurrence raises an operator feed alert and waits for a leased engine instance.
The per-transition profile is the only ownership selector. Review and delivery
workers are registered at every startup so a runtime rollback can use them
without restarting the host. OrchestratorPrep, MetaCycle, and AutoIntervention
continue to run in the monolith when other profiles select Engine; they have
no engine occurrences yet.

AGT-3234: every real gate outcome of a batch-deferred review is one
`GateResult` occurrence: a batch release (`batch-result:`), a per-task gate
pass, a containment or an already-integrated member (`batch-fallback:`), and a
red per-task gate (`batch-fallback-red:`). A red batch run ends for each member
in one of these, through isolation or the per-task gate. A red per-task gate
states a card round budget of zero, because the batch route grants no
automatic fix round, so the monolith's escalation and the engine's decision
compare on the same facts. In Engine mode the red outcome waits for the engine
like a release; the applier then moves the card and, for a decision that does
not release the member, resolves its queue entry and removes its batch
ownership, as the monolith's escalation does.

The review worker rechecks the current owner after long gate work and before
each lane move. A monolith-owned resume checks again at integration dequeue,
at the publication fence, and before its final lane move. An ownership deferral
is not a settled archive result; rollback leaves that generation eligible for
reconciliation. The backend policy-call boundary inspects compiled calls to
decision-library policy methods through `MonolithTransitionAdapter`, including
calls written with a type alias or `using static`.

AGT-3060 (I18) moves the background deciders into this library. The operator
decision of 15.10.2026 keeps the per-transition switch as the single selector
of who decides: every profile defaults to `Monolith`, and with `Monolith`
selected the monolith decides in process through this library; no host forces
a mode. The run-liveness, stale-progress, steer-timeout, timeout-salvage,
gate-environment-retry, delivery-chain, cause-breaker, intake-routing and
acceptance-rail rules live here (`RunLivenessPolicy`, `StaleProgressPolicy`,
`SteerTimeoutPolicy`, `RunTimeoutSalvageContinuationPolicy`,
`GateEnvironmentRetryPolicy`, `GateCancellationPolicy`, `DeliveryChainPolicy`, `CauseBreakerClosePolicy`,
`IntakeRoutingPolicy`, `AcceptanceRailDecisionPolicy`). `AiOperatorDecisionPolicy`
is the AI Operator role of AGT-W73 O1 A: it selects the decider's policy,
classifies the effect with an autonomy class, and limits it (A0 and A1 act, A2
becomes a proposal, A3 is asked of a person). The monolith submits frozen facts
through `AiOperatorDecisionGateway`, whose profile is `OperatorDecision`. On
`Monolith` the gateway returns this library's decision at once; on `Engine` it
records an `OperatorDecision` occurrence, the engine's `AiOperatorDecisionLoop`
returns the receipt, and the decider's `IAiOperatorEffect` applies it after
checking the lane and the frozen facts again. One active occurrence exists per
card and decider; a periodic decider keys each period bucket, an event-triggered
one keys its subject, and the residual deciders key a hash of their facts. The
inventory of every hosted service and its owner is
[monolith-hosted-services.md](../architecture/monolith-hosted-services.md).
`OrchestrationEngineReadiness` reports, but never holds: while a profile selects
the engine, `GET /healthz/orchestration-engine` answers `ready` or
`engine-not-live`.
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
their token envelope and estimated priced cost. Reclaimed claims keep billed
completions from the replaced runner as separate calls attributed to the same
job; duplicate reports do not add spend. A refused job stays `cap-exhausted`. Each call carries an
envelope of at most 8,000 input and 1,000 output tokens. The runner refuses a
prompt whose conservative estimate with framing margin exceeds it before
spawning. It passes the output cap to the CLI (`CLAUDE_CODE_MAX_OUTPUT_TOKENS`,
Codex `model_max_output_tokens`) and the total context window to Codex as
`model_context_window`. The
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
OD-S6 adds API-owned immutable rule drafts under
`<TaskRepository>/.metadata/orchestrator-rules`. Records are written through a
flushed temporary file. An unreadable record is moved to `quarantine/` with a
logged error at startup, and the rule view reports `degraded`; the store never
fails the runner completion path that reads it. An activation is an append-only
actor, reason, version and time record; rollback creates and activates a new
version. The workspace version governs host-wide decisions. A project overlay
can add or replace soft signals but cannot govern a host-wide trigger or edit
the hard authority floors. The Rules editor drafts, replays up to 100 receipts
from 30 days, labels the model result counterfactual, and requires explicit
activation. An occurrence freezes the active rule version in its grounds, so
later activation does not rewrite its receipt. A card occurrence without an
active rule version keeps the unchanged OD-S1 policy version and prompt.

`DecisionRulePolicy.Validate` refuses a rule whose trigger is outside the closed
list that live paths read (`step-transition` and `CodingCompletion` for cards,
`capacity-check` for hosts), a rule without candidate actions, and an `ask` or
`hold` rule whose fact is not a known fact of its trigger. `ask` and `hold` may
not read `review.last-outcome`: it is observed at the last review, not at the
occurrence, so it is usually stale; a `signal` rule may read it. A card-trigger
version, and a project overlay merged with its workspace rules, must leave at
least one available non-ask coding completion alternative within the A0/A1
floor for every deterministic outcome, with and without a settled envelope, and
without a verified tree, result SHA or ref; only known facts count as present.
In practice a card version keeps `retry` allowed at A0/A1. A rule
model must be one the configured `Orchestration:DecisionModel` CLI serves and
that the Token Economy model catalogue rates frontier and selectable. The rule
replaces only the model of the configured route, so its thinking level and the
alternate transport retry stay configured.

Studio owns `GET /api/workspaces/{workspaceId}/decision-rules` and its
`POST /versions`, `POST /versions/{versionId}/activate`,
`POST /versions/{versionId}/rollback`, and
`GET /versions/{versionId}/dry-run` subroutes. Each accepts an optional
`projectId` query parameter for a project overlay. The dry run replays only
receipts the caller may read, and matches a receipt's registry project name to
the overlay's project id. Studio receipt and spend
queries at `/api/orchestration/decision-receipts` and its `/spend` subroute
accept `workspaceId` as well as `hostId` and card or project filters.

The engine can submit a card-free `capacity-check` host occurrence through
`/api/v1/orchestration/host-decisions`. Its scope has a host and workspace id
with null card, attempt and fence. The host id must name an enrolled, active
runner host; `cadence` (`scheduled` or `event`) is required; `observedAt` must
be within 10 minutes of the server clock; facts come from the capacity check
vocabulary. When the host-day bucket of the cadence is exhausted, the route
answers 429 `host-day-cap-exhausted`; frozen occurrences that still wait for
their job reserve one call and the full per-call envelope in the day of their
`admittedAt`, the server clock at admission. Studio pins the
workspace rules, builds the configured decision route, bounds source references
and freezes the grounds with prompt version `od-s6.host-occurrence.v1`.

With a standalone Task Server (`TaskServer:BaseUrl`), the Task Server owns
workspaces and Studio cannot resolve their ids. The rule routes and
`host-decisions` then answer 409 `decision-rules-not-on-proxy-profile`. Studio
keeps only the receipt GET routes, the engine receipt routes and
`/api/runner/orchestrator-decisions` open in that profile; the operator-intent
POST moves cards through the legacy task store and stays closed. The engine
keeps stage claims on `SERVER_URL`. `DECISION_PLANE_URL` selects a separate
receipt plane for pending grounds, frozen input, decision jobs and receipts; any
value that differs from `SERVER_URL`, loopback included, requires
`DECISION_PLANE_CLIENT_CREDENTIAL` or `DECISION_PLANE_CLIENT_CREDENTIAL_FILE`,
and the Task Server credential is never sent there. Runner hosts that execute
decision jobs must reach the receipt plane's `/api/runner/orchestrator-decisions`
claim and completion routes. The engine's receipt loop records zero to three
offered actions with assessment and report content. The receipt remains
immutable; host history is
`/api/orchestration/decision-receipts?hostId=...` and the same receipt enters
workspace spend. A host decision action in this seam is proposed for the
Fluid capacity authority adapter; Fluid capacity owns scheduling, allocation,
utilization and action application.
The engine waits for the runner-hosted model job, records an active receipt,
then submits the chosen action through stage completion. The Task Server
validates the receipt against the frozen grounds, the current authority epoch
and fence, the settled run and exact result SHA/ref, and the offered action's
A0 or A1 floor before applying an effect. A valid `prepare(review-subject)`
creates review; `retry(card:ready)` requeues even when the settled run has no
result envelope; `adapt(salvage-handoff)` requires an envelope and pins the
immutable result ref as the next round's base; `checkpoint(verified-tree)`
writes `results/checkpoint.md` with the last verified SHA and retains the engine
wait marker while the card is held in Progress.
Unusable output and unavailable or capped calls record `ask(operator:ai)`
with the call status and no-call reason, and leave the card in its source lane.
Measured usage over the per-call token envelope also records an unavailable ask,
even when the model response contains a valid action. If the Task Server rejects
an active choice against the receipt or current authority, it persists an AI
Operator ask with the rejection reason and completes the occurrence without
applying that choice. A turn dispatch failure leaves the saved ask available for
recovery and does not prevent occurrence completion. A held AI Operator turn that
fails or is parked is retried once after 30 seconds; a second failure records
the reason in the card timeline and the project operator feed for human review.
An A2/A3 proposal cannot mutate the card. The active profile can be disabled
for a compatibility rollback with
`Orchestration:DecisionModel:ActiveCodingCompletion=false`. It is **off by default**
even when `Orchestration:Transitions:CodingCompletion=Engine`; activation requires
the explicit per-transition setting
`Orchestration:DecisionModel:ActiveCodingCompletion=true` after shadow and
engine mode have run on Stable. Existing Engine occurrences keep their frozen
decision mode through completion. Held decisions persist an AI Operator ask for
restart recovery, and a selected salvage base is staged before the Ready move
and removed if the fenced move fails. The receipt's outcome link shows the
later applied stage action. `Orchestration:DecisionModel:Cli`, `Model`
and `ThinkingLevel` default to `codex`, `gpt-6-sol` and `medium`; the engine
reads `SHADOW_DECISION_CONCURRENCY` (2) and `SHADOW_DECISION_TIMEOUT_SECONDS`
(900). An engine-held provider credential remains a separate follow-up decision
if claim latency proves too high.

An explicit Human Operator answer to a card ask is separate from shadow
dispatch. The audit entry starts as `recorded, not applied` and names its actor.
Raise and Cut choose a card run limit in minutes. The monolith applies that
limit to later remote run specs. Their direction is checked against the coding
runner's reported timeout, or the last applied card limit. A remote runner
without a reported timeout cannot accept a limit change. Decision receipt Stop
is temporarily unavailable: the route returns `409 stop-not-available` and the
card view hides the control until all remote attempt exit paths can hold the card.
A shadow receipt never dispatches itself. The bus projection includes billed alternate attempts, and
late billed calls emit additional usage evidence.

AGT-3066 adds eight pure signal producers for gate duration, review aspect
duration, Watcher contingent consumption, coding run elapsed time, salvage
continuation, review reissue, Git call use, and gate environment retries.
Coding-completion grounds include the measurements available at settlement;
missing review-command, Watcher, review-reissue journal, Git-call, and gate-fingerprint evidence is
named as an omission. The gate signal carries its derivation sentence and
sample count; an absent cohort is stated rather than presenting a made-up p95.
Signals do not choose an action. The per-command kill ceilings, finite re-entry,
fencing and exact-result checks remain mechanical.

The coding process emergency limit resolves project override, workspace default,
then the 24-hour platform constant. The effective value and source travel in
the remote run spec and runner log. A Human Operator card limit still wins for
the next run; Raise and Cut accept 1 through 10,080 minutes so Raise remains
usable above the 24-hour default. The runner watchdog retains its typed timeout outcome and
existing salvage ref and SHA publication. RB-S2 stepped shaping budgets are
superseded and were not implemented.

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

An engine `DeferToBatch` decision selects no lane; its batch admission is the
whole application. Like the monolith report path, the applier admits only a
card in Auto Review, records the review verification minutes, enqueues the
settled review at the accepted report's receive time, and runs the per-task
emergency gate when the queue refuses it. The occurrence keeps the lane its
report found and the decision policy assumes Auto Review, so a card that the
report found in another lane, a card that left Auto Review since, and a review
that is no longer current complete the occurrence with an operator-feed reason;
any other failed admission keeps the occurrence `applying` for recovery to
retry. Until 17.10.2026 that branch returned before the admission. The recovery
service therefore re-arms, at most four per pass, a completed non-shadow
decision that is still its card's newest `ReviewReport` occurrence while the
card waits in Auto Review on its current review, the batch queue has no entry
for that review, and no batch-gate ownership records a batch, a passed per-task
gate or a containment. Ownership alone does not count: a bare marker left by a
stop before the queue entry, and an active per-task gate marker whose gate was
interrupted, are admitted again, which re-enqueues the review or resumes the
gate within its start budget. A pass checks the host stop between admissions,
and the service waits for a running pass when it stops. It also clears wait
markers that name a cancelled occurrence or a completed one without an active
choice, and rereads the occurrence after each marker write.

## Applier contract and canary

On 16.10.2026 ReviewReport switched from Shadow to Engine after 39 matching
shadow decisions, and for 16 hours `MonolithOrchestrationApplier` completed
every engine `DeferToBatch` decision without its batch admission. Shadow mode
compares decision receipts and never runs the applier, so no streak can prove
that an engine decision is applied. AGT-3218 adds the applier contract and the
applier canary.

`EngineApplierContract` (`backend/Features/Runner/ApplierCanary/`) is the one
action to effect table. `For(TransitionAction)` has no default arm and the
backend build treats CS8509 as an error, so a new action without an applier
effect fails the build. `ProfileActions` lists the actions each rollout profile
can complete. Every row except `BatchPlan` also owes a cleared wait marker
(`WaitMarkerCleared`) and a completed occurrence that holds the engine receipt
and its stage result (`DecisionReceipt`).

| Action | Profiles | Lane | Effect the applier owes |
|---|---|---|---|
| CreateReview | CodingCompletion | payload lane (Auto Review) | Lane move; a review request creates the review attempt and the review subject |
| FindingRound, ConcernRound | ReviewReport | Ready | Lane move, keyed continuation prompt, quality-loop timeline receipt |
| AcceptAndIntegrate | ReviewReport | Human Review | Delivery settlement journal at `IntegrationSettled`, lane move, park verdict; after an agent round handoff the journal at `LaneSettled` and no lane move |
| DeferToBatch | ReviewReport | none | Batch admission: ownership and pending record, the per-task gate's keyed lane write to Human Review (`batch-fallback:<review>`) or its escalation after a recorded run with that review's fallback evidence, or an operator-feed reason why none was owed |
| HumanReview | CodingCompletion, ReviewReport | Human Review | Lane move; a review occurrence records its park verdict |
| Escalate | CodingCompletion, ReviewReport, GateResult | Escalated | Lane move |
| ReviewInfrastructureRetry | ReviewReport | none | Scheduled review infrastructure retry; lane kept |
| DegradeWithFollowUp | ReviewReport | Human Review | Linked follow-up in Ready, settlement journal, lane move, park verdict |
| SweepIgnore, SweepHold | operator sweep | none | Lane kept |
| SweepRound, SweepWaitForPerson | operator sweep | stage lane | Lane move when they reach a transition profile |
| SalvageContinuation | CodingCompletion | Ready | Lane move |
| ReturnToReady | CodingCompletion, GateResult | Ready | Lane move, keyed continuation prompt |
| Continue | CodingCompletion, GateResult | stage lane | Lane move to the payload lane; a passed gate without one keeps its lane and records `Continue`; the gate handler's next pass dispatches the aspect review and records `engine_gate_dispatched` on the card timeline |
| Halt | ReviewReport | none | Lane kept |
| BatchPlan | BatchPlan | none | The plan is stored as the durable intent of its scope; the batch tick takes it and records `applied`, `expired`, `rejected` or `superseded` |

The stage lane is the payload's `targetState`, or Escalated for an escalating
stage action. `EngineApplierContract.Owed` narrows a row by that lane and the
payload: a park verdict is owed only by a review occurrence parked in Human
Review, a review attempt only by a coding completion that carries a review
request, and a gate dispatch only by a gate decision without a lane. When the
integration of an accepted or degraded review raised a review-input decision,
the applier stops before the lane move and the park verdict, which that
decision owns: the decision then owes its follow-up and settlement journal and
the `ReviewInputHandoff` (the wait marker names the review-input occurrence, or
that occurrence was raised for the card while the decision applied). When the
integration ended in `Conflict` or `AgentRoundRequired` and started the
automatic agent round, `IntegrationAgentRoundService` has already moved the
card to Ready inside `EnqueueAsync`. The applier reads the card again after the
integration, settles the journal to `LaneSettled`, clears the wait marker and
completes; a retry finds the same handoff before the source-state check. The
decision then owes its follow-up, journal and the `AgentRoundHandoff`, and the
next delivery is reviewed again. One predicate,
`IntegrationAgentRoundHandoff.Find`, ties the handoff to this review's own
integration for the applier and the probe alike:

- the settlement journal names the review;
- the card is no longer in the source lane (a card back in Auto Review
  integrates again, whatever rows an earlier review left);
- on the applier's first pass the result is `Conflict` or
  `AgentRoundRequired` without `AutomaticRecoveryBudgetUsed` (an exhausted
  budget leaves the card in Auto Review, and the applier moves it to Human
  Review with its park verdict);
- the row is the `IntegrationAgentRoundService` row of the reviewed SHA
  (reason `delivery-attribution-ambiguous`, automatic, no `source`;
  acceptance-rail and recovery-service rows carry the SHA too), written at or
  after the card's newest `enqueued` or `agent-round-re-entry` lane row, so the
  round of an earlier review of the same delivery does not count.
`EngineApplierEffectProbe` reads these effects without writing and without a
scanner rescan. A lane move counts when the card is in the lane or its
timeline holds the keyed `LaneChanged` row of the decision, so a card that a
runner picked up again still verifies. The engine batch plan intent and the
gate dispatch receipt are files, so a restart inside the canary window keeps
both. The batch tick claims an intent by an atomic rename; an intent it cannot
read stays as an `.unreadable` file for an operator instead of being deleted
without a receipt.

The contract tests (`BatchGateEngineApplierContractTests`, partial
`BatchGatePilotServiceTests`) run the real applier on an engine-completed
occurrence for every profile action, assert each owed effect directly, and
check that the probe agrees; the review-input handoff and the agent round
after a conflict (`AcceptAndIntegrate`, real `IntegrationAgentRoundService`)
have their own cases. A retried accept seeded with the state the applier
before the fix left proves the handoff completes; a second review of the same
delivery whose integration conflicts with the round budget spent is parked in
Human Review; a retry with a stale round row integrates. The
gate case records the handler's dispatch receipt after the applier ran,
because the test host does not run the review orchestrator;
`ReviewDecisionOrchestratorCompletionGateTests` drives the real gate
handler's second pass in engine mode and asserts the receipt.
`EngineApplierContractGuardTests`
(`Category=Guard`) fails when a transition action has no row, when a row's lane
differs from the applier's lane rule, or when a profile action has no contract
case.

The canary rule:

- `ready -> engine` is refused with
  `orchestration-profile-applier-canary-required` unless an applier canary for
  the profile passed on this instance. The instance is the host name and build
  commit (`Orchestration:InstanceId` overrides it), because a canary proves the
  applier code that ran it.
- `ready -> canary` selects the engine (`MonolithTransitionSwitch` reads
  Engine) and watches the first `Orchestration:ApplierCanary:Decisions`
  (default 3) non-shadow engine decisions of the profile. A decision is watched
  when the engine completes it, before the applier runs, so a decision that
  never applies is watched too; `engine_rollback` completions do not count.
  Every switch to `canary` or `engine` starts a new watch.
- `EngineApplierCanaryService` probes each watched decision every five
  seconds. Only a verified row check of a completed occurrence counts toward
  the required number. A BatchPlan counts only once its occurrence completed
  with the engine receipt: the applier writes the durable intent before the
  occurrence completes, so an intent of an applying plan is no applied
  decision. These are `not-counted`, and the next engine decision takes their
  place:
  - an ask, a checkpoint, a review-input handoff and an agent round handoff;
  - a refusal by design: the card left the source lane by a move this
    decision's write key did not write (a person, a runner, another
    integration round; the evidence names the actor and the lane-change
    cause), so the applier refuses it with
    `orchestration-source-state-mismatch`. A DeferToBatch admission refuses
    no lane and is exempt. The agent round of this review's own integration
    is no refusal but the decision's effect: unapplied at the deadline, it is
    missing;
  - a passed gate decision whose card left Auto Review, got a pending intent
    or a new run attempt before the gate handler's next pass, which therefore
    never records `engine_gate_dispatched`;
  - a decision the probe fails on: the probe exception is no applier defect,
    so the operator feed gets an `applier-canary-probe` alert and the profile
    keeps its state. One failing observation leaves the others checked.

  When the required decisions show their effects, the canary passes on this
  instance and a `canary` profile becomes `engine` with an `applier-canary`
  switch receipt.
- A decision the applier has not completed waits until
  `Orchestration:ApplierCanary:ApplyDeadlineSeconds` (default 10800, three
  hours) after it was observed: AcceptAndIntegrate and DegradeWithFollowUp
  await the whole integration, queue and delivery gate included, and a
  DeferToBatch admission can run a per-task gate. Once completed, its effects
  must show within `Orchestration:ApplierCanary:WindowSeconds` (default 120)
  after `CompletedAt`; an effect that a later pass writes (the gate dispatch)
  gets the apply deadline after `CompletedAt` instead. After the integration of
  an unapplied accept ended (its terminal lane row, or the agent round of this
  review), the applier gets at least one effect window, even past the apply
  deadline.
- Some checks wait on something that is no applier defect (`waitingOn`): an
  integration in flight (the card's integration lane shows the delivery
  `enqueued`, `agent-round-re-entry` or `dequeued` since the observation, and
  no `ready-to-publish`, `ejected`, `published` or `handed-off` row; the
  per-project queue is serial and
  every delivery runs its gate) and a card missing from the scanner snapshot.
  They wait, and at the apply deadline they are `not-counted`, never missing.
- Restart safety: after a start the canary judges nothing until the recovery
  service finished one pass over the applying occurrences
  (`MonolithOrchestrationStore.ApplyingRecoveryPasses`), and every deadline
  counts from the later of the observation (or `CompletedAt`) and the process
  start, so downtime is left out. Until that pass the canary status reports
  `awaitingRecoveryPass`, and each step of the recovery loop fails alone, so a
  failing activation or AI Operator recovery no longer stops the passes.
- When a decision misses its deadline, the canary fails, this instance loses
  any earlier pass (a direct switch back to engine needs a new canary), the
  profile rolls back (`rolled-back`, so the monolith decides pending
  occurrences), and the card's operator feed gets an `applier-canary` alert
  with the missing effects, the probe evidence, the card and its lane, and the
  manual redrive. The rollback re-decides only pending and leased occurrences:
  a completed occurrence is final, and an applying one keeps retrying the
  engine decision, so the alert tells the operator where to move the card.
- `force-engine` is the emergency override: it switches any profile that is
  not `engine` to `engine` without a passed canary and without the shadow
  check. It needs an operator reason (and the Owner role on a networked host),
  its switch receipt carries `override: true`, and the host logs
  `orchestration-profile-engine-forced`. Its canary still watches and alerts,
  but does not roll the profile back.

`GET /api/orchestration/rollout/profiles` reports per profile the streak, the
applier contract status (`covered` when every profile action has a row), the
canary status with its observations (and whether an override started it),
and the last effect check with its basis, completion time, and whether it
waits for a downstream effect.
`GET /api/orchestration/rollout/applier-contract` returns the table. The
Transition decisions panel on the engine settings page shows the same fields
and offers Start applier canary for a Ready profile; Switch to engine appears
only after a canary passed on this instance. Force engine (emergency override)
asks for confirmation before it sends `force-engine`.

The engine batch plan intent is written and taken under one lock, and the
batch tick claims the intent file by an atomic rename, so a plan the applier
writes while the tick takes the older one stays for the next tick (or gets a
`superseded` receipt) and is never deleted unread. Tick receipts in
`engine-intent-receipts/` are kept for seven days and pruned when a new
receipt is recorded.

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
that the source guard rejects reintroduced rules. The policy-call guard inspects
compiled backend calls to decision-library policy and breaker types, regardless
of method name, return type, alias, or `using static`. It rejects calls outside
`MonolithTransitionAdapter`.
Host branches may inspect a returned decision to apply it or validate
persistence results. Add new transition
policies to the owned-policy inventory and put their facts and rules in this
library. Broader background-service movement remains I18.

`MonolithDecisionBoundaryTests` (AGT-3060, `Category=Guard`) fails when a host
forces a decision mode, when a registered hosted service is missing from the
inventory, when a `decide` row is neither an AI Operator decider reaching
`AiOperatorDecisionGateway` nor a switched profile reaching
`MonolithTransitionSwitch` (apart from the closed set of retained monolith
deciders), when an AI Operator decider calls a library rule around the switch,
or when a backend host constructs a transition receipt. No host-defined rule
baseline is accepted. OrchestratorPrep, MetaCycle and AutoIntervention remain
on the monolith path while they have no engine occurrence.
The review post-processing worker, startup recovery, evidence projection, and
settlement reconciliation stay registered across profile changes. Each checks
the current transition owner before acting on a card. Coding post-processing and recovery wait while either `CodingCompletion` or
`ReviewReport` selects Engine; evidence projection and settlement wait while
`ReviewReport` selects Engine. The delivery resume entry point also waits if
either profile selects Engine. Recovery keeps waiting for a
runtime rollback, and recurring settlement and review sweeps recover journaled
work after ownership returns to Monolith.
Run-liveness receipt application remeasures log activity before a demotion, so
activity added after submission restores the card's grace period.

Run the guard with:

```sh
dotnet test backend.Tests/OrchestratorApi.Tests.csproj --no-build --filter 'FullyQualifiedName~.Architecture.|Category=Guard'
```
