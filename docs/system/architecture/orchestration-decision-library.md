# Orchestration decision library

AGT-3057 implements AGT-W65 I10, using operator options D14 A, D15 A,
D16 A, D17 A and D18 A. The dependency deliveries AGT-3052, AGT-3054 and
AGT-3056 are ancestors of the delivery checkout.

`orchestration-decisions/Orchestration.Decisions.csproj` owns the extracted
transition policies. Its only project dependency is `TaskServer.Contracts`.
Inputs are immutable facts and counters; outputs carry actions, reasons,
findings, the reviewed continuation base, candidate authority floors and
receipt fields. The library does not read files, run processes, call HTTP,
read configuration or consult a clock.

The monolith still invokes these policies until I17 transfers each transition
to the engine. Host code gathers facts and applies decisions under existing
lease and task-version authority. Lane writes, journals, follow-up creation,
prompts and Git operations remain in host apply code. This slice neither starts
the engine against the monolith nor moves background services. D15 and D16
retain separate process roles; no deployment or runner-placement change is
introduced here.

## Decision surfaces

- Coding completion: lane selection and review creation admission.
- Remote review: follow-up classification, lifetime budget, card budget,
  infrastructure retry, batch deferral and delivery gate admission.
- Local review: the same follow-up and budget policies, council reaction,
  reissue loop breaker, completion, evidence and solution-quality gate rulings.
- Sweeps: trigger admission and guard ordering for fix rounds, gate triage and
  salvage; timeout and lost-worker continuation limits.
- Standalone orchestration: stage rules, repeated-aspect identity and settlement.
  Recurrence keys on the aspect, independent of the finding's wording. A spent
  non-test block creates one linked follow-up; build and evidence blocks escalate.
  Finding rounds preserve the reviewed ref and SHA across claims and mark the
  base as required using the existing runner continuation reason contract.

Public names retained from the monolith are compatibility names, not assembly
ownership. For example, `AgentStudio.Runner.ReviewRoundBudgetPolicy` now lives
in `Orchestration.Decisions.dll`. Storage classes remain in the backend.
The batch planner and advisor remain owned by the existing pure
`TaskServer.Contracts/BatchGate` module; the transition library consumes the
frozen batch-deferral fact.

## Action vocabulary and AGT-W73 mapping

The following mapping covers this slice's requested AGT-W73 union subset.
AGT-W73 also describes observe and checkpoint actions; this library does not
introduce those operations. Its deterministic candidate is a degraded-mode
choice, not the model-backed decision planned by AGT-W73.

| Typed action | AGT-W73 intent | Minimum dispatch authority |
|---|---|---|
| CreateReview | prepare | Fenced attempt |
| FindingRound | adapt | Fenced attempt and reviewed delivery |
| ConcernRound | adapt | Fenced attempt and reviewed delivery |
| AcceptAndIntegrate | continue | Fenced attempt and delivery gate |
| DeferToBatch | defer | Fenced attempt and frozen batch plan |
| HumanReview | ask | Operator for subsequent resolution |
| Escalate | ask | Operator for subsequent resolution |
| ReviewInfrastructureRetry | retry | Fenced attempt and remaining retry budget |
| DegradeWithFollowUp | adapt | Fenced attempt; linked follow-up must be persisted |
| Continue | continue | Fenced attempt |
| ReturnToReady | continue | Fenced attempt |
| SweepFixRound | adapt | Fenced attempt and unhandled subject |
| SweepGateRound | adapt | Fenced attempt and product gate finding |
| SalvageContinuation | retry | Fenced attempt and published salvage |
| Hold | defer | Automatic observation |
| Ignore | halt | Automatic observation; no lane mutation |

Authority floors describe prerequisites and who resolves an escalation. They
do not grant a lease, waive a test gate or authorize a model to act as an
operator. Applications must still fence the write against current authority.
`DecisionReceipt` records the card and attempt, rule, used/allowed rounds,
aspects and required follow-up linkage. `ContinuationBaseFact` carries the
review attempt and exact ref/SHA, keeping D17 evidence attached to an action.

## Verification and architecture guard

`DecisionGoldenTests` executes the same scenario through the monolith, Task
Server and engine adapters and compares serialized decisions, including receipts
and continuation bases. Cases cover pass, actionable concerns, product blocks,
test blocks, spent budgets, infrastructure failure, integration-branch defect,
batch deferral, missing build proof and sweep guards. An engine-handler test
also verifies that the shared receipt survives its output envelope.

`TransitionDecisionBoundaryTests` runs in the backend test project's architecture
and Guard filter. It checks policy assembly ownership, the library's dependency
and effect boundaries, expression-only forwarding adapters, and the extracted
handler decision seams. A new transition policy belongs in this library and
must be added to its ownership inventory and scenario matrix. Do not reintroduce
an outcome switch or budget rule into an adapter or apply handler.

Focused commands:

```sh
dotnet test orchestrator-engine.Tests/OrchestratorEngine.Tests.csproj --no-build --filter 'FullyQualifiedName~DecisionGoldenTests|FullyQualifiedName~StageHandlerTests'
dotnet test backend.Tests/OrchestratorApi.Tests.csproj --no-build --filter 'FullyQualifiedName~.Architecture.|Category=Guard'
```

Run the same guard filter on the runner and Task Server test commands listed in
`.agent-studio/project.yml`. Existing review-plane, budget, sweep, lease and
batch test classes protect the monolith compatibility path.
