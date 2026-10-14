# Orchestration decision library

AGT-3057 implements AGT-W65 D14 A as an extraction slice, with D17 A and D18 A.
`orchestration-decisions/Orchestration.Decisions.csproj` owns the deterministic
transition policies. It references only TaskServer.Contracts and the .NET
runtime. It reads no files, configuration, clock, processes, or HTTP services.
Every library type uses `AgentStudio.OrchestrationDecisions`. State, timeline,
failure-domain, and outcome constants are shared from TaskServer.Contracts,
so the hosts and library read the same persisted tokens. Verdicts classified
`BlockWithoutCitation` do not open a finding round or count as a repeated block.

The monolith still invokes these policies and applies their decisions. I16 and
I17 supply the remote orchestration boundary and switch the decision caller to
the engine. I18 moves hosted background scheduling. This library does not start
an engine process or change deployment roles.

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
actions. Completing the standalone execution loop remains I11.

The monolith retains the existing delivered-round and recurrence limits as well
as the lifetime automatic-round cap. Extraction does not replenish a counter or
change the existing order of effects. Test blocks remain non-degradable, and an
unactionable concern does not spend an automatic round. Batch planning and
advisor policies remain in the AGT-3052 contracts package.

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
