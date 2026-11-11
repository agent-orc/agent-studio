# Review Domain Map

Acceptance checks the first-parent line after a delivery's verified tree. A
server-authored document-key discovery commit is gate-neutral only when its
first-parent diff changes existing `docs/**/workbench.json` descriptors solely
by adding `key`. New managed commits carry an operation trailer; legacy
commits require the Agent Studio author and committer identity. Acceptance
records the inspected commit SHA in the card's
completion evidence. Every other later commit still needs a passed gate.
Batch candidates and direct integration merges assign keys to descriptors
changed by the delivery before the gate tests the resulting tree. Catalogue
discovery keeps its managed key mutation for descriptors outside a gated
delivery.

Version: 2026-11-09
Status: System-of-record map for Remote Review material, semantic verdicts, and grading.

## Aspect prompt cache

The review worker bounds every per-card and package agent aspect's final prompt to 800,000
characters after it adds the authoritative delivery diff. It keeps changed
template and style files first, limits each diff file to 40,000 characters,
and limits the total diff to 580,000 characters. Visual review includes at
most 12 screenshots of at most 2 MiB each. The prompt names omissions, and
the command's review evidence carries a `review-input-budget-<step>.txt`
artifact when material was omitted. A CLI refusal before a model verdict,
including `input_too_large`, is `ReviewInfra` with a named cause.

The four stock Claude aspects freeze a common review-context prefix containing
the brief, delivery acceptance scope, project and card context, diff summary,
and results inventory. The review host inserts the authoritative merge-base diff
at the end of that prefix, before aspect-specific instructions. The first
Claude aspect completes before the remaining aspects start in parallel, so its
cache write can serve their reads. Custom prompt overrides and Codex aspects
retain their existing prompt layout and scheduling. The frozen prompt bytes
version this layout for review-plan digests. Each grade report shows cache
creation and read tokens by aspect and as a review total.

If the first aspect reports neither cache creation nor cache read tokens, the
runner logs `review-aspect-cache-unconfirmed` and runs the remaining aspects
serially (parallelism 1) instead of in parallel, so they do not issue
simultaneous cold cache writes.

To measure the effect, open the grade report `remote-review-grade-<attempt>.md`
in the card's job folder (also linked from the review timeline) for at least
five Claude-aspect reviews that ran after the deploy. Its "Aspect cache usage"
table lists `Cache creation tokens` and `Cache read tokens` per aspect and a
`Review total` row. Compare per review:

- the first aspect (`requirement-fit` in the stock order) against the three
  later aspects, and
- the review totals against the baseline from the review settlements of
  2 November 2026: about 60,000 cache creation tokens per aspect run, about
  240,000 per review, with cache reads near zero.

The shared prefix works when the first aspect carries most of the review's
cache creation tokens and each later aspect shows cache read tokens of about
the first aspect's creation size with only a small creation count of its own
(its aspect instructions), so the review total of creation tokens drops to
roughly one prefix plus four small suffixes. A later aspect that again creates
about the full prefix, or a runner log line `review-aspect-cache-unconfirmed`,
means the cache was not shared for that review.

Remote coding plans freeze a bounded `results/` evidence view beside the brief
and diff context. It lists up to 40 active files with sizes, quotes heads of
small text and CSV/JSON summaries and tails of logs within 12 files, 4,000
characters per file and 24,000 characters total, and names image files. A
SHA-256 over every eligible file's path, size and content makes the sealed
aspect digest change even when the changed file falls outside the excerpt
budget. `results/history/` and filesystem links are excluded. Credential
redaction applies to displayed names and excerpts. Requirement-fit and
tests-and-evidence may cite `results/<file>`; a missing-evidence finding names
the inspected diff and results inventory or files. Truncated excerpts do not
establish absence from a full artifact.

## Review report transport budget

The review host caps embedded evidence at 8 MiB per artifact and the serialized
report at 24 MiB, below the report route's explicit 25 MiB body limit. An
oversized log is represented by its full byte count and SHA-256 plus a separate
head-and-tail excerpt with an elision marker and its own digest. The original
artifact remains on the review host. `artifactsReduced` marks this wire shape so
preparation failures retain their classification when full log bytes are omitted.
A 413 response or three consecutive 502
responses triggers one reduced resend with embedded artifact bytes omitted;
the outcome, failure classification, and aspect verdicts are unchanged. The
host persists this choice in the review slot so reattachment cannot restart
the same oversized submission cycle. Transient failures of the reduced report
(including 502, 503, and timeouts) retry the same payload up to five times;
exhaustion is recorded as `ReportDeliveryExhausted`. Another 413 or a permanent
refusal ends delivery immediately. In-process
backend test hosts keep full file logs but omit routine information and known
fixture warnings from verification stdout. Unexpected warnings and all errors
remain visible there.

Remote Review materializes exact result SHAs through the runner host's bounded
source object cache (D8 A, AGT-3319). A canonical remote fetch populates a
missing commit; a bundle-only subject downloads its digest-checked bundle only
on a cache miss. Each attempt receives an independent object copy and proves
its HEAD and tree before review commands run. A failing cache integrity check
evicts and refetches the entry.

## Review economics (AGT-3278)

`GET /api/projects/{projectName}/token-usage/reviews` folds the canonical
deduplicated project token ledger into delivery review attempts. Query filters
are `days` (1 to 90, default 30), `model`, `level`, and `host`. The endpoint
returns median and nearest-rank p90 tokens and historical list-price USD per
delivery review, accepted-card review rounds and spend, review share of card
tokens, step and model/level splits, daily 7-day and 30-day median series, and
the underlying review call rows. The ledger snapshot and the derived response
are cached for at most 60 seconds against the task index generation.

Remote Review receipts now preserve `support:remote-review:{attempt}:step:{stepId}`
on each agent command. Replaying an attempt replaces all of its step rows.
Local aspect and grade usage emits project-scoped ledger rows with the pipeline
attempt and step; a local call made without a pipeline attempt remains a
distinct legacy round. Review decision calls are attributed to the card.
Older receipts retain their attempt attribution but appear under
`review, step unknown`. Calls without a historical catalog price remain in
token statistics and are omitted from USD samples. The project overview and
usage cockpit show the same read-only tiles and link each tile to the matching
review rows. Miss-rate feedback awaits AGT-3269.

## Deep review selection (AGT-3268)

`RemoteReviewPlanBuilder` freezes a `deepReviewDecision` receipt on each coding
delivery's ReviewSubject. `DeepReview:Mode` defaults to `shadow`: the receipt
records the selection and route, but no command runs. `active` adds one
`post-deep-review` semantic command to the remote review plan. The review host
executes it in the same read-only, exact-SHA workspace as the aspects. Its
command evidence includes model, reasoning level, token usage, and host identity,
and its verdict is reported as `deep-review` beside the aspect verdicts.
The run-completion timeline names the receipt, and report projection records a
`post-deep-review-decision` pipeline row before the deep-review verdict row.

The engine policy in `orchestration-decisions/DeepReviewPolicy.cs` selects from
the card's Token Economy correctness floor, changed paths and line count, prior
blocks, gate failures, delivered rounds, and quota admission. It defaults to
`gpt-6-sol` at `medium`; P0, critical correctness floors, and touched security
or auth feature code, attempt authority, transition decisions, or deep-review
policy require `xhigh`. Unknown diff size is recorded in the receipt without
selecting review. Available quota never raises the effort. A capped route can suppress
discretionary review, but a critical review retains its floor and waits for
quota. The plan refuses a quota fallback that would replace its frozen route.
Quota-deferred attempts have a short claim cooldown so other queued reviews can run.
The AGT-3068 rules editor accepts the `deep-review` trigger with numeric
`review.changed-lines`, `review.previous-blocks`, `review.gate-failures`, and
`review.delivered-rounds` facts. A matching `prepare` rule selects review;
`observe` declines it. The critical floor cannot be waived. Model and reasoning
level can be configured within the validated catalogue and the critical floor.

The plan stops deep review after three delivered rounds or the lower project
review limit. Aspect and deep findings with the same cited evidence and missing
contract form one fix item. A third consecutive block of the same cited deep
finding escalates to a person rather than opening another automatic fix round. The existing card
round and reissue budgets still govern every review result.

## Package review in shadow (AGT-3400 S4)

Dossier `docs/review-gate-packages/index.html` decides that the context-heavy
review aspects run once per Dossier wave. Slice S4 implements that package
review in shadow only. It uses the recommended options: PK3 A (cards of one
`dossier.path`; cards without a Dossier stay per card), PK4 R-B (build, tests,
deterministic checks and requirement fit stay per card) and PK5 A (60 minutes,
at most 4 members, within the material budget, never waiting for coding).

- **Setting.** `PUT /api/projects/{projectName}/review-package` with
  `{"mode":"off"|"shadow"}` (Owner role) stores `review.package`. The default
  is `off`, and `off` clears the section. `dossier`, the live mode of S5, is
  rejected. `GET /api/projects/settings` reports `reviewPackage`.
- **Subject kind `package`.** A package subject holds the Dossier path, the
  members in stack order (task, run attempt, result SHA, card-check round,
  material size, acceptance criteria), a membership digest and a frozen plan.
  The standalone Task Server stores it in `review_package_subjects` and
  `review_package_members`; the monolith stores it in its instance folder
  `review-packages/`. The per-card `review_subjects` table, card subject
  sidecar and attempt authority are unchanged. A member delivery joins at most
  one package.
- **Formation tick.** `ReviewPackageFormationHostedService` runs every 30
  seconds beside review admission. A card's check passed when every card-check
  step (tool commands, requirement fit) of one review round of its current
  result SHA passed, as the S2 baseline defines it. The pure
  `ReviewPackageFormationPolicy` groups such cards by `dossier.path`: members
  whose check passed within 60 minutes of the first, at most 4, at most 200
  files (union of paths) and 12,000 lines. It waits only for cards in Auto
  Review whose check has not passed, never for a card in a coding lane, and a
  lone member is a package of one. A card that alone exceeds the budget stays
  per card. Each decision is appended once to
  `review-packages/plans/<project>.jsonl` (`GET .../review-packages/formation`).
- **Stacked candidate.** The review host fetches every member result and the
  integration ref, then applies the members on the tip in stack order:
  `references.dependsOn` first, then the Dossier implementation index. Each
  member's own commits are cherry-picked; a range with a merge commit, or a
  pick that conflicts, is merged as one merge commit instead. Each member's
  diff runs from the candidate before it to the candidate after it, so every
  hunk has one owner. A member that does not stack reports `stack-conflict`; a
  union above the budget reports `over-budget` and is never truncated.
- **Aspects.** Code quality, documentation impact, tests and evidence and the
  new `aspect-cross-card-consistency` run once on the package. Each prompt is
  `review-package-aspect.md` around the aspect's own rules, with the CLI, model
  and thinking level of the members' per-card plan. Only the cross-card prompt
  carries the Dossier excerpt, once. The grade is computed per member from the
  package verdicts, as the remote per-card grade is.
- **Structured output.** Every aspect ends with one `package-review` JSON block:
  one verdict per member with its acceptance criteria, and findings with file
  and line. A finding without both is discarded. The line's hunk owner owns the
  finding; a line no member wrote is cross-card and goes to the later member in
  stack order, naming the others as related. A member block that no finding of
  that aspect cites is recorded as `concerns`.
- **Shadow guarantees.** A package report is admitted under
  `ReviewPackageReportPolicy` and projected into each member's
  `results/package-review-observations.jsonl` as review observations with level
  `package`. That journal is separate from `review-observations.jsonl`, so
  miss-rate cohorts, review depth and the Token Economy export never read it.
  No package path moves a lane, writes a verdict or settles an attempt; the
  per-card review runs unchanged.
- **Shadow report.** `GET /api/projects/{projectName}/review-packages/shadow-report?days=14`
  compares, per member and aspect, the package verdict and findings with the
  per-card verdict and findings of the same aspect in the round that passed the
  card check, and the package token totals with the per-card tokens of the same
  aspects. `cardBlocksMissed` counts per-card blocks the package did not block.
- **Review host.** When its per-card claim is empty, the review daemon claims
  at most one package at a time (`/api/v1/runners/{id}/review-package-claims`),
  beside the per-card slots and without taking one of them.
  `RUNNER_PACKAGE_REVIEW=0` turns this off. Shadow packages read public
  repositories only.

## Review host placement (D19 A, AGT-3069)

Remote Review subjects retain the source coding host, and each claimed attempt
retains its review host. A review claim on the coding host yields to another
idle eligible review executor on a different host. When no such executor is
idle, the same host claims immediately. Set
`PUT /api/projects/{projectName}/review-host-requirement` with
`{"required":true}` to require different hosts for that project. Its frozen
review plan carries `requireDifferentHostFailureDomain`, so a same-host claim
then waits even when it is the only executor. The empty claim reports
`different-review-host-required` with the coding host and the wait cause.
Setting `required` to `false` restores the default preference. Standalone
subjects freeze this choice when created; the monolith also checks the current
project requirement before claim. A lease already granted remains fenced to
its chosen host.

## Quality Studio shadow evidence

The local post-core Quality Studio Angular rule pass records canonical sensor
findings in the task's review evidence log. Each entry carries the Quality
Studio `ruleId`, source location, and analysis artifact. The pipeline step is
report-only while the Quality Studio verdict contract is compared in shadow;
its findings do not override the existing review verdict or trigger a reissue.

## Lifetime review budget

Each card carries `review-round-budget.json`, an attempt-id deduped count of
delivered local and Remote Review rounds. The count survives operator requeues,
`/continue`, and evidence epoch changes. Existing cards seed it from their
review artifacts. Project settings expose `maxDeliveredReviewRounds` (default
4) and `maxAutoReissueAttempts` (default 2) through
`PUT /api/projects/{projectName}/review-round-budgets`. The delivered limit is
lifetime per card; the reissue limit applies to the current review epoch.
The task detail shows the delivered round count, limit, and aspect
that spent the budget. Automatic continuation admission also checks the
per-card `CardRoundBudget` (default 4), which counts reissues, review finding
and concern reopens, and automatic continuations across all epochs.

An aspect's consecutive block streak keys on its aspect id, independent of the
finding summary or epoch. After two prior consecutive blocks, or when the
lifetime round or reissue budget is spent, a further non-build aspect block is
recorded as `concerns`. With zero automatic reissues allowed, an initial block
still follows the ordinary escalation path until the aspect streak or lifetime
round limit binds. One linked follow-up card carries all open findings;
`references.raisedFollowUps` points to it from the delivery. The card has one
review-budget follow-up; later degraded rounds append their findings to it.
Engine review settlements reuse an open `engine-follow-up` card linked to the
same source, including when a later settlement has a different run id. They
append only findings absent from its brief and write a
`review_follow_up_routed` event on the source timeline naming the receiving
card. Once that follow-up reaches Completed, a later settlement may create a
new one.
The delivery then follows the ordinary acceptance
and integration rules. A build/test failure or tests-and-evidence block is
never degraded. When the per-card budget is spent, such a block moves to
Escalated with a receipt naming used and allowed rounds and open aspects.
A spent non-test block degrades to concerns with one linked follow-up.
For Remote Review, the accepted settlement journal carries the original
blocking verdicts and budget decision. Restart reconciliation restores the
round ledger and linked follow-up before it resumes delivery integration.
The same journal records a finding-round continuation before settlement.
Reconciliation completes its delivered-round ledger, Ready move, concern
ledger, follow-up files, prompt note and timeline reopen once after a restart.
It decides from the ledger before the lane: the timeline receipt marks the
continuation complete in any lane, and a `lane_changed` row keyed by the
continuation idempotency key means only the remaining writes run. Only a card
that never took the move and still sits in 4-auto-review is moved, so a card
that later returns to review is never bounced by the settled attempt.

## Concern fix and scoped re-review contract

`ReviewFollowUpPolicy` is the plane-neutral decision used by local aspect review
and Remote Review. A blocking verdict uses the review-finding route while its
lifetime budget remains available. A real,
actionable `concerns` verdict gets one automatic coding round by default; the
project setting `maxReviewConcernRounds` sets the bound and `0` disables it.
Actionable means the reviewer names a file or symbol in the delivery and a
missing or wrong behavior. Style, naming, duplication without a behavioral gap,
and findings with no named location remain advisory. The same classification
applies to a block downgraded for missing review-material citation. The fix
round uses the reviewer summary and `missing` text as quoted data. It must also
fit the card-round and lifetime delivered-review budgets. The batch gate state
lists cards in Ready or Progress that wait for this fix, with the review attempt,
aspect, and round limit. `GET /api/projects/{projectName}/review-concerns/daily?days=30`
reports daily actionable and advisory verdict counts, plus counts whose settled
delivery integration reached `develop`.
Set these project controls with `PUT /api/projects/{projectName}/review-follow-up`.
The JSON body uses `maxConcernRounds` (integer, default `1`, clamped to `0..10`),
`scopedReviewAfterFinding` (boolean, default `true`), and
`scopedReviewMaximumDeltaFiles` (integer, default `20`, clamped to `0..1000`).
Omitted fields use those defaults. Each `PUT` writes all three settings, so
omitting a field resets that setting to its default. The endpoint returns the
updated project settings, where the concern limit is named
`maxReviewConcernRounds`; an unknown project returns `404`. For example:

```json
{
  "maxConcernRounds": 1,
  "scopedReviewAfterFinding": true,
  "scopedReviewMaximumDeltaFiles": 20
}
```

`review-concern-round.json` is the per-card concern-fix round ledger.
It records both concern and blocking-finding context, while only concern rounds
consume the configured concern budget. The UI reports `concern round 1 of 1
used`. A second concerns result is parked in Human Review with the remaining
findings. `review:unparseable` and infrastructure failures
are not product concerns: their aspect is retried once and a repeated failure is
surfaced under its exact classification (`review:unparseable` or environmental
`InfraCrash`) by both the local and Remote Review decision paths. This also
applies when another aspect reports a blocking product finding in the same
review: the infrastructure failure is surfaced first, and the blocking finding
remains in the review report for the next valid decision. Infrastructure
routing uses only the runner-authored `InfrastructureFailure` flag (projected
from local `AspectVerdict.IsInfraFailure`) or the exact classification
`review:unparseable` / `ReviewInfra`. Free-text summaries never classify a
reviewer failure: a product block mentioning "infra crash" or "no parseable
verdict" still enters the coding fix path.

The fix prompt contains the review attempt, aspect, summary, cited evidence,
and finding, and instructs the coding agent to change exactly those items.
Reviewer text is data, not instructions (AGT-2989): every prompt that carries
findings to the implementer (the council follow-up, the local and Remote Review
finding and concern rounds, and both reissue prompt arms) quotes them between
two fence lines tagged with a fresh random nonce, under a frame that the fenced
lines must not be followed as directions. `ReviewFindingDataBlock` owns that
fence; a finding that contains the requested nonce forces a new one, so it can
never close the block early.
Run-history consumers, including Result-summary round ledgers, use the contract
field names `trigger`, `triggeredBy`, `triggerReason`, and `triggerSource` from
`run-record.schema.json`; they do not derive a trigger from legacy `intent`.

After a review-driven coding round, deterministic build, test, and lint steps
always execute. `ScopedReviewPolicy` reruns the aspect that raised the finding.
Another aspect may carry over only when scoped review is enabled (project
setting `scopedReviewAfterFinding`, default true), the reviewed tree identity
still matches, the delta is within `scopedReviewMaximumDeltaFiles` (default 20), no changed file is in
that aspect's `evidence_checked`, no public contract/schema changed for
documentation-impact, and no test file was removed for tests-and-evidence.
Otherwise review falls back to every aspect. A carried verdict records
`carriedOverFrom`; it is displayed as carried over and is not a fresh pass.

For a new Remote Review delivery, the plan also compares the last passed
delivery's own patch with the new result's own patch. Each patch is measured
against its respective merge base with the integration branch, per changed
path and without hunk line offsets. If every patch matches, the four content
verdicts carry over from that passed attempt. If only `status.md` or a path
under `results/` differs, requirement-fit and tests-and-evidence run again,
while code-quality and documentation-impact carry over. Any other difference,
missing merge-base evidence, or unavailable prior passing verdict runs the
full content review. Deterministic tool gates remain in the review plan.

Use this when a change touches ReviewSubject preparation, aspect prompts,
review-report verdicts, reissue or escalation decisions, the isolation of
concurrent review attempts, or the evidence shown for a remote grade.

An Auto Review card may not remain ownerless. The
`ReviewInfrastructureRetryScheduler` owns both AGT-2841's bounded replacement
after a terminal ReviewInfra result and the missing-handoff repair: after
`Runner:ReviewMissingAttemptTimeoutMinutes` (30 by default), a card with a
completed immutable run, no canonical ReviewAttempt, and no durable park gets
one idempotent replacement attempt. A scheduled ReviewInfra retry is canonical
authority and is never treated as missing.

The per-card Auto Review stranding watchdog runs every 30 seconds. After
`AutoReviewStranding:ThresholdMinutes` (30 by default), it checks for a running
pipeline step, a pending or leased ReviewAttempt, a pending or active formed
batch, a queued round, and a queued post-processing step. A card with none gets
a durable intervention receipt in the existing stall-intervention feed and
its timeline. Report-only cards restart post-processing; coding cards with a
completed run request a fresh ReviewAttempt; a passed review resumes delivery
integration or re-enters the batch gate only when its source RunAttempt and
result SHA match the current completed delivery and the completed review tested
that SHA. Pending and active formed batches own a card only when their run and
result SHA match the current delivery; a pending entry must also match its
current review attempt. An older review starts a fresh review for the new
delivery. If no safe step can be started, the
card moves to Escalated with the reason. Each intervention in a lane visit has
its own receipt. After a successful intervention, the watchdog waits another
threshold interval before intervening again. It can advance from review recovery
to batch recovery in the same visit. If the same recovery leaves the card
ownerless again, it escalates the card with the reason.
The threshold starts at the latest ownership change, including a review terminal,
a batch state event, or a recovery intervention, rather than at lane entry. A
current-delivery infrastructure failure with no scheduled retry gets one through
the existing authority scheduler while its two-retry budget remains. A scheduled
retry counts as an owner. Once the budget is spent, escalation names the review
classification and summary.

When the delivery-chain reconciler finds a stale acceptance or integration
verdict, it returns the card to Auto Review and requests a fresh review at
once. A delivery conflict queues the existing integration recovery round,
which steers the next coding attempt against `develop` and records the round
on the timeline.

## Versioned review-library steps

Concept cards on the remote plane receive one semantic `concept-fit` aspect in
addition to the existing build and lint commands. It uses the shared aspect
runner, verdict sentinel, citation policy, and review grade. Its frozen prompt
compares the delivered Dossier in the Result-SHA diff with the current
`prompt.md` (bounded to 64,000 characters in the rendered prompt): required
sections, operator directions, recommendations for open
decisions, and implementation-card source data. A contradiction is a blocking
finding. The project pipeline-step setting `aspect-concept-fit` resolves its
enabled state, prompt override, CLI, model, and thinking level like other
aspects. Its default route is the bounded support aspect route from
`PipelineStepModelDefaults`; build and lint keep their existing plans.

The concept ReviewSubject stores the SHA-256 of the complete `prompt.md` in
both its requirements identity and frozen review plan. Its frozen plan also
records `taskMode: concept`, independently of the brief hash. The standalone
Task Server uses that marker, with legacy concept-plan evidence as a fallback,
to apply concept coverage even when the hash is absent. Report settlement
compares that hash with the card's current brief. A missing concept verdict,
missing brief, or changed brief settles as `Inconclusive` with a named reason,
never `Pass`. A fresh attempt must judge the current brief. Concept verdicts
are always rerun; scoped carry-over does not apply to `concept-fit`.
The remote grade prints the brief hash and every applicable aspect skipped by
configuration or condition, with its reason. Passing summaries cover executed
aspects only, so a command-only report cannot claim that all applicable content
checks passed.

Completion reads the settled review journal for the current RunAttempt, rather
than treating an overall `Pass` or a build/lint grade as content review. The
remote plan binds coding `requirement-fit` to the authored brief version and
concept `concept-fit` to the reviewed brief hash. A delivery contract without
an accepted claim refuses completion, so each accepted card carries this
content receipt. The Human Review delivery panel shows delivery and current
brief versions, the content verdict and its citations, and ran/skipped aspects.
A concept block or concern marks its
brief/Dossier comparison as a contradiction or gap. Local requirement-fit
artifacts carry a brief-context sidecar; older unbound artifacts remain visible
but cannot establish a current-brief pass. Local pickup stamps
`.metadata/local-run-brief-version` before the CLI starts; the aspect runner
copies that run version into `content-review-context.json` beside the verdict,
so editing the brief during a run cannot silently relabel its delivery.

`RemoteReviewPlanBuilder` resolves verification, gate, and semantic aspect
commands on the server. New plans set `ReviewPlanDto.LibraryVersion = 1`. When
the exact Result-SHA is known, both review stores seal each command and
preparation command with `ReviewLibraryStepDto`: step id, version, SHA-256 digest,
input subject SHA, required capabilities, command timeout, review slot, and the
.NET build `maxcpucount:2` limit. The digest covers the command payload, step id,
version, and subject SHA after resource normalization. A semantic aspect may
rederive its effective timeout after the exact diff is appended, within the
sealed 7,200-second ceiling. The command limit and .NET build limit sit inside
the host's review-slot resource envelope. A plan with version 0 remains readable
for older in-flight attempts; unknown versions are rejected.
Deploy a version-one-capable review runner before issuing new version-one plans;
older hosts can finish legacy attempts but cannot claim the new steps.

Only a review registration advertising `review:library-step:v1` and all step
requirements can claim a version-one subject. Requirements include the command's
declared CLI/provider and detected .NET, Node, or Playwright toolchain. The
toolchain keys come from `ReviewLibraryStepPolicy.ToolchainRequirements`, and the
review registration adds every key from that table the host probe finds
(AGT-2987). An executor that still lacks a key receives the typed empty reason
`unclaimable-plan-requirements` with the missing keys; see the
[review lane runbook](../../operations/review-lane-runbook.md). Coding and review registrations
have separate identities and capacity. The runner checks the sealed plan before
materializing the exact subject. Each command evidence row echoes the step
envelope beside its actual SHA, timings, exit status, output hashes, and
executor attribution. Both Task Server implementations reject a changed step
digest in a fenced report. The existing report idempotency key, lease/fence,
verdict citations, and cleanup endpoint remain authoritative. A new plan may
be written only into a pending attempt or a new eligible attempt; a leased
attempt keeps its plan through report retry and worker adoption.

## Review material contract

### Screenshot-backed visual review

The standard feature, bug, and task review plans include `aspect-visual-review`
when the delivery changes a file under `frontend/` ending in `.html`, `.scss`,
or `.css`. The aspect is recorded in the same pipeline catalogue and verdict
ledger as requirement fit, code quality, documentation impact, and tests and
evidence. Its catalogue route selects the Codex image-capable support model
through the central pipeline model resolver; project step overrides remain
subject to the normal CLI/model compatibility check.

The executor fetches bounded image artifacts from the source run's `results/`
delivery, preserves the screenshot names, and places read-only copies beside
the exact-SHA review checkout. The visual prompt receives those paths, the
authoritative diff, the hard visual rules, and the relevant admin design rules.
After screenshots take priority over before screenshots at both the remote
100-image cap and the local eight-image attachment cap. Both caps count only
images with valid bounded headers, so unavailable or invalid artifacts cannot
crowd out a later valid after screenshot. Each artifact read is isolated, so a
failed remote artifact cannot stop later images from loading.
The prompt lists readable image paths first, then up to eight artifact failure
details and a count of further failures, so corrupt artifacts cannot bury a
valid screenshot path in the review context.
An artifact must have a PNG, JPEG, or WebP header matching its extension before
it uses an image slot; matching size and digest alone do not prove the header is
valid. The server does not decode pixels, so later image viewing can still
reject damaged payloads. Candidates are selected by extension, not by the
uploaded media type, so a `.webp` that an older runner sent as
`application/octet-stream` still counts once its header is proven.
The review daemon captures the screenshots while it prepares the attempt, but
the detached review worker runs the plan in a separate process. The daemon
therefore writes `visual-evidence/index.json` beside the copies (artifact name,
file name, header-proven media type, and the `light` or `dark` theme read from
the name), and the worker reloads that index when it adopts the prepared
workspace (AGT-3718). A copy missing at reload is named as unavailable in the
prompt.
Name delivered screenshots under `results/` by surface, stage, viewport width,
and theme, for example `bars/before-1920-light.png` and
`bars/after-1920-light.png`. Use the same suffix for matching before and after
views. A `before` path segment or filename marker (`before-`, `-before`, or
equivalent underscore or dot separators) marks baseline evidence. An unmarked
image counts as an after view. At least one readable after image is required;
include both light and dark themes and a narrow viewport when those surfaces
change.
It opens each screenshot, compares matching before and after views, and checks
layout, spacing, separation, alignment, clipping, and contrast across available
themes and widths. A visual concern or block cites both the screenshot and the
affected region. If a frontend template or style changed without a readable
screenshot, or only before screenshots, the executor guarantees a `concerns`
verdict naming the missing after evidence. Nonvisual changes skip this aspect.

`runner/RemoteReviewWorkspace.cs` materializes the immutable delivery
Result-SHA in a disposable candidate checkout. Before the first semantic aspect
runs, it freshly fetches the frozen plan's integration ref and resolves the Git
merge-base between that ref and the Result-SHA. It then appends the same
authoritative material to every semantic aspect prompt:

- the integration ref, resolved merge-base SHA, and delivery Result-SHA;
- the changed-file list from `git diff --name-only <merge-base> <result-sha>`;
- the unified delivery diff from
  `git diff --find-renames --unified=3 --full-index <merge-base> <result-sha>`.

Both diffs run with pinned settings (`MergeBaseDiffMaterial.PinnedConfiguration`:
no prefix overrides, no colour, no textconv, full object names in `index`
lines), so the bytes do not depend on the Git configuration or object count of
the host that computes them.

Since AGT-3318 the material is a diff object (AGT-3017 D1) keyed by repository,
merge-base SHA, Result-SHA, options and renderer version. The fenced source
facts producer on the coding runner host computes it at result handoff, with
path history and blame for the touched files, and publishes it to the Task
Server (`/api/v1/source-facts/{repositoryId}/diffs`). The review reads that
object first. Only a missing object falls back to the local `git diff`, through
the same `MergeBaseDiffMaterial.ComputeAsync` the producer runs, so one base and
head render byte-identical material from either path. A fallback records a
`review-material-miss` demand for the producer and a read report with source
`fallback`; every read reports its time, and
`GET /api/v1/source-facts/review-material/timings` returns the p50 and p95 per
source.

The semantic prompt must not rely on the review agent discovering this material
with tools. The frozen template created by
`backend/Features/Runner/RemoteReviewPlanBuilder.cs` supplies task and status
context; `RemoteReviewWorkspace` supplies Git evidence from the exact candidate
checkout at execution time. A semantic plan therefore requires an integration
ref even when no build command uses baseline comparison.

The prompt budget is 200 changed files and 12,000 unified-diff lines. Within
that budget the list and diff are complete. When either limit is exceeded, the
prompt contains a visible `REVIEW_DIFF_TRUNCATED` marker stating the shown and
total file and line counts plus the configured budget. Truncation is never
silent.

## Aspect budget contract

`contracts/TaskServer.Contracts/ReviewAspectBudgetPolicy.cs` derives one aspect
call's wall-clock budget as `base(cli) * weight(model) * weight(thinking level)
+ material`, clamped to 60..7200 seconds. Each term answers a question an
operator can check: which toolchain, how much thinking the routed model does,
and how much there is to read. The material term is bounded so one enormous diff
cannot buy an unbounded budget.

The plan builder freezes a budget derived from the authored prompt;
`RemoteReviewWorkspace` re-derives it once it has appended the authoritative
diff and runs the larger of the two, because the frozen prompt does not yet
carry that material. The executed budget, never the frozen one, is what the
command evidence and any violation report.

A review that runs out of budget is an infrastructure fact about a model on a
host, not a verdict about the change. The failure is classified `AspectTimeout`,
and its sentence names the model and the limit
(`violated review-command budget on model '<model>'`). A command that produces
no output at all for its silence window
(`RUNNER_COMMAND_SILENCE_WATCHDOG_SECONDS`, default 600 s, engaged only when it
is strictly tighter than the command budget) is killed as `CommandStalled`
rather than holding its review slot for the rest of the budget. That watchdog and
the no-CPU-progress watchdog below are separate detectors and neither subsumes
the other: silence catches a command that keeps burning CPU without ever
producing a line, no-CPU-progress catches a tree blocked on a host-shared
handle.

A blocked tree can trip both watchdogs. `StallDetection` in
`runner/RemoteReviewWorkspace.cs` lets whichever detector actually reaches its
own threshold first claim the kill (an atomic first-write-wins record), so the
report always names the detector that fired - `detector=silence` or
`detector=no-cpu-progress` - together with its effective window and what was
measured (AGT-2851). Earlier the silence catch always took priority whenever
its own poll loop had independently observed silence, which happened to match
reality only because the silence window defaulted tighter than the
no-CPU-progress window; an operator raising the silence window past the
no-CPU-progress window stopped changing which detector actually ended the
command, and the report kept saying "silence" regardless.

`contracts/TaskServer.Contracts/ReviewPlanResourcePolicy.cs` caps every
`dotnet build` and `dotnet test` in a frozen plan at `-maxcpucount:2` and starts
them with `-nodeReuse:false`. The cap is deterministic rather than derived from
measured host load: the plan is frozen before an executor claims it, so a
load-derived cap would make the fenced command depend on when it was built, and
host load is already a separate admission gate
(`runner/ReviewSlotAdmissionPolicy.cs`).

## Attempt isolation contract

One host runs several fenced ReviewAttempts at once. Isolation is what makes
that safe, and it has three layers.

**Filesystem and network.** `RemoteReviewWorkspace` roots every writable path
under the attempt directory (`HOME`, `XDG_CACHE_HOME`, `npm_config_cache`,
`PIP_CACHE_DIR`, `CARGO_HOME`, `GRADLE_USER_HOME`, `DOTNET_CLI_HOME`). The lease
supplies the attempt's `ResourceNamespace` for containers and databases and an
eight-port window from `PortBase`. The baseline worktree gets its own sub-root
inside the same attempt, so candidate and baseline never share these caches
either. Two paths are deliberately outside the attempt directory (AGT-3429):

- `TMPDIR` is a short, owner-only root `/tmp/rv-<12 hex>` named by a digest
  of the attempt directory (`runner/ReviewAttemptTemp.cs`): `c` for the
  candidate and one 12-character command segment per baseline or clean repeat.
  Below the attempt directory a socket path such as Chrome's
  `$TMPDIR/org.chromium.Chromium.XXXXXX/SingletonSocket` exceeded the 107-byte
  Unix socket limit and ChromeHeadless could not start. The root's `.owner`
  file names its attempt; cleanup removes it with the attempt and the retention
  sweep removes one whose attempt is gone.
- `NUGET_PACKAGES` and `NUGET_SCRATCH` are the host-stable
  `.dependency-cache/nuget-packages` and `nuget-scratch`, shared by every
  attempt and role. The dependency cache preserves `**/obj`, and a cached
  `project.assets.json` must name a package folder that still exists. The
  folder is content addressed by package id and version, so candidate and
  baseline resolve identical bits for identical versions, and the shared
  scratch folder holds NuGet's extraction locks. See
  [pipeline.md](pipeline.md) for what a dependency-cache hit may skip.

Before each candidate tool command, the review workspace checks every prepared
dependency scope for its directory and lockfile marker. A missing scope reruns
preparation and records the miss in command evidence. An environment diagnosis
evicts the candidate cache entry and invalidates its scope markers, then the
next candidate command prepares those scopes again. It keeps the dependency
directories in place while candidate commands remain. The baseline comparison
only saves its own cache. If preparation cannot restore a vanished directory,
the infrastructure result names the scope and the last step observed removing
it. An exit 127 after a command removes its dependency directory uses the same
classification rather than reporting a missing toolchain.

**Build servers.** Path isolation does not cover them. On Linux, Roslyn's
`VBCSCompiler` listens on `/tmp/<pipename>` derived from the compiler directory
and the user name, and reusable MSBuild worker nodes listen on
`/tmp/MSBuild<pid>`. Both are host-global for one service account and one SDK,
and both keep the working directory of whichever attempt started them, so an
attempt that has been cleaned up leaves a server answering from a deleted tree
(AGT-2831). `runner/ReviewBuildServerIsolation.cs` therefore removes the shared
servers rather than renaming them: `MSBUILDDISABLENODEREUSE=1`,
`DOTNET_CLI_USE_MSBUILD_SERVER=0`, `UseSharedCompilation=false`, plus an
attempt-local `MSBUILDDEBUGPATH`. It is applied last to the candidate, baseline
and preparation environments, so an immutable plan can never re-enable a
host-shared build server. The attempt's `ReviewEnvironmentDto` carries
`buildServers=per-attempt` as proof.

**Progress.** A command that blocks on a host-shared handle consumes no CPU,
prints nothing and never exits, so a wall-clock budget cannot tell it apart
from a slow build. `runner/CommandProgressWatchdog.cs` samples the command's
whole process tree from `/proc` and kills it when the tree fails to burn one
percent of one core within its effective no-CPU-progress window. That window is
never smaller than `RUNNER_REVIEW_NO_CPU_PROGRESS_SECONDS` (default 900, `0`
disables) but also never smaller than half the executing command's own budget
(`RemoteReviewWorkspace.NoCpuProgressWindow`, AGT-2851): a healthy integration
suite run with `ParallelizeTestCollections=false` can sit near 0% CPU for long
stretches between test classes, and a fixed 900 s floor killed reviews on this
host that would otherwise have finished in 20-25 minutes. Such a command is
evidenced with signal `no-progress` and reported as `ReviewInfra/NoCpuProgress`
- checked before baseline comparison, so a hang is never parsed into test
failures and graded as a product regression. On a host without `/proc` the
sampler returns nothing and the watchdog stays inert rather than guessing a
kill.

**Failure attribution.** A failed verify command is attributed before it is
parsed. `runner/ReviewInfraAttributionPolicy.cs` decides whether the output
carries the torn-down-`/tmp` signature table (`MSB1025`, `SocketException (99)`,
a NuGet `mkdtemp("/tmp/.dotnet.` `ENOENT`) as process-level evidence, in which
case the attempt is settled as `ReviewInfra`/`TmpMountTornDown`, or whether the
command's own test results own the verdict. The policy reads the tool's own
lines only: a signature quoted inside a printed test result, be it the display
name, its inline data, or the indented error message and stack trace below it,
is test-owned text and never classifies the run as infrastructure, and a run
whose summary counts at least one failed test (`Failed: N`) is a product
failure whatever else the output contains. Before AGT-2857 the table was
matched against the whole command output, so one passing theory case named
`...(reply: "System.IO.IOException: mkdtemp("/tmp/.dotnet.AbC123") == nullptr;
errno == ENOENT")` hid the single red test of review attempt
`review_849e4484f1454ea2bfcc8b4d7be6dd31`, kept that failure off the card, and
suppressed the AGT-2841 replacement attempt. Infrastructure attribution stays
available exactly where it was meant to apply: a runner that aborted, or one
that never produced a test summary at all.

`ReviewPlanResourcePolicy` also adds `--logger "console;verbosity=normal"` to
every review `dotnet test` invocation, so the silence watchdog above has real
per-test progress lines to reset its clock against through a long quiet suite
instead of relying only on the default console logger's start/end output.

Safe parallelism and the procedure for raising it live in
[linux-runner-host.md](../../operations/setup/linux-runner-host.md#review-parallelism-and-build-server-isolation).

## Private repository access (AGT-3077)

The review clone carries no credential, because review commands execute
candidate code. Before AGT-3077 that made every private repository
unreviewable: WEB-26/27/28 failed their clone with `could not read Username`,
classified `SnapshotUnavailable`, and escalated after the infrastructure retry
budget.

- **Plan.** The effective repository visibility is sealed into every review
  plan as `ReviewPlanDto.PrivateRepository` by `RemoteReviewPlanBuilder`. It
  combines the project setting `repositoryVisibility` (`public` by default,
  `PUT /api/projects/{project}/repository-visibility`) with what the connected
  GitHub App reports (AGT-3408, `ReviewRepositoryAccessPolicy.EffectiveVisibility`,
  read through `ProjectRepositoryVisibility`): a repository an adopted
  installation lists follows GitHub's `private` flag, an explicit `private`
  setting still wins, and a repository GitHub reports private is never planned
  public. `GitHubAppBroker` caches the installations' repository lists, refreshed
  by the host credential service (monolith) or `GitHubAppVisibilityRefreshService`
  (standalone Task Server); a failed listing keeps the last answer.
  Task Server repository registrations may also declare `repositoryVisibility`.
  The existing project setting is copied to matching registrations when it
  changes and reconciled on startup and periodically, including registrations
  created before the setting was copied. The credential inventory uses the
  registered `private` value to require read access on every review host before
  any review attempt exists.
  Changing it re-plans every ReviewAttempt still queued for the project, like a
  build-profile or pipeline-step edit, so an attempt queued before the flip
  carries the new visibility.
- **Retry.** An infrastructure retry takes the current visibility, not the
  failed attempt's. The backend builds every successor's plan afresh. The
  standalone Task Server keeps one plan per subject, so `InsertReviewRetryAsync`
  renews its `privateRepository` from the registration and the GitHub report
  (`ReviewRepositoryAccessPolicy.CurrentPrivateRepository`) when it queues the
  successor; with neither known, the sealed value stays. A new subject of a
  repository GitHub reports private is stored private.
- **Claim filter.** `ReviewRepositoryAccessPolicy` (shared contract) turns a
  private plan plus the subject's repository URL into the required key
  `review:repository-read:<host>/<owner>/<repo>` (lower case, no `.git`, same
  key for https, ssh and scp spellings). The backend queue filter
  (`ClaimNextReview`, `ListUnclaimableReviews`), its post-selection recheck in
  the claim endpoint, and the standalone Task Server
  (`MissingSubjectCapabilities`) all refuse an executor that did not register
  the key. The empty claim carries `unclaimable-plan-requirements`, names the
  key, and tells the operator which setting is missing. The attempt stays
  queued and no retry budget is spent.
- **Plan-less attempts.** A lease-expiry successor and a settled-run recovery
  attempt carry no plan, and an attempt may carry no repository URL; the
  claim endpoint builds both at hand-out. Before selection it therefore
  resolves, per open review task, the project visibility and repository URL
  (`ReviewClaimRepositoryFact`), and `ReviewClaimRepositoryAccessPolicy` judges
  the attempt as the executor would receive it (a sealed plan and a stored URL
  outrank the project). Such an attempt is skipped, not leased and deferred,
  so an executor without the key keeps claiming every other project's reviews.
- **Host placement.** The coding host yields a review (AGT-3069) only to an
  idle host that also holds this repository's proven read key, judged by the
  same `ReviewClaimRepositoryAccessPolicy` facts. A website card coded on the
  only host with the key is therefore reviewed there, instead of being yielded
  to a keyless review host whose claim filter skips it. The standalone Task
  Server judges the alternative host the same way
  (`HasIdleEligibleDifferentReviewHostAsync` checks its proven read keys with
  `MissingSubjectCapabilities`).
- **Read proof.** A line in the credentials file is not a working token. The
  review daemon proves each credential with a credentialed `git ls-remote`
  (`runner/ReviewRepositoryReadProbe.cs`, re-proved every 10 minutes when
  ready, every 2 minutes when not) before each capability advertisement and
  advertises the key `ready` or `unavailable` with the git error. Registration
  names the configured key; both Task Server implementations admit it only
  while the current advertisement reports a fresh `ready` proof. An expired,
  revoked, unapproved or under-scoped token therefore refuses the claim with
  the named key instead of failing the clone.
- **Failed private clone.** A `SnapshotUnavailable` failure of a private
  subject re-proves that repository's read key on the next advertisement
  instead of reporting a `repository-access` failure, so a bad token pauses
  only that repository, not the executor for every project.
- **Executor profile.** `RUNNER_REVIEW_GIT_CREDENTIALS_FILE` holds one
  read-only, repository-scoped `https://` credential per line
  (`runner/ReviewRepositoryCredentials.cs`). The review daemon registers and
  advertises one read key per line and passes only the file path to the
  detached worker.
- **Credential boundary.** `RemoteReviewWorkspace` hands the credential only to
  the git clone and fetch child processes (`CredentialedGitArguments`,
  `CredentialedGitEnvironment`). Inherited helpers, the attempt's global
  config, hooks, ask-pass, redirects and foreign transports are switched off,
  and the inline helper answers only for the scoped protocol, host and path.
  `ProcessEnvironment()`, which every build, test, preparation and aspect
  command uses, never contains it. A private clone is not blob-filtered, so no
  later git command lazily fetches with missing credentials. The late
  integration-ref fetch for baseline comparison runs in a fresh runner-owned
  repository and the workspace copies the objects over the local transport,
  so configuration written by review commands never meets the credential.
- **Held credential wins.** A review host that holds a credential for exactly
  the subject's repository (App token or credentials file) fetches with it
  whatever the plan says (AGT-3408), so a repository wrongly planned public
  costs nothing on such a host. This widens no access: the lookup matches one
  canonical repository, and only the clone and fetch processes see it.
- **Public repositories** without a held credential keep the credential-free
  clone. A public plan does not depend on the credentials file: a file that
  turns unreadable or malformed after start-up fails only private reviews. A
  credential-free clone that fails with an authentication signature names the
  missing read access in its `SnapshotUnavailable` detail.

Operator setup: [linux-runner-host.md](../../operations/setup/linux-runner-host.md#private-repositories).
Tests: `runner.Tests/ReviewPrivateRepositoryTests.cs` (clone through a local
Basic-auth smart-HTTP server), `runner.Tests/ReviewRepositoryAccessPolicyTests.cs`,
`backend.Tests/PrivateRepositoryReviewClaimTests.cs`,
`backend.Tests/GitHubAppBrokerTests.cs`, and
`task-server.Tests/RemoteReviewAuthorityTests.PrivateRepository.cs`.

## Review-plane parallelism

`AdaptiveReviewParallelismAdvisor` owns the review plane's recommended
parallelism. The recommendation rides the minutely capability advertisement as
`RoleMaxParallelism`, and `TaskServerClient.RoleMaxParallelism` is the ceiling
`RemoteReviewDaemon` actually claims against - the review counterpart of the
coding runner's central capacity. A lowered ceiling stops new claims; it never
cancels an active slot.

`RUNNER_MAX_PARALLELISM` is deprecated as the live review control. It seeds the
ceiling until the first advertisement is answered and remains the fallback for
an older server; a later file change does not replace the recommendation. It
still bounds the recommendation from above: the capability-advertisement
endpoint clamps `AdaptiveReviewParallelismAdvisor`'s one global number to each
executor's own registered bootstrap before answering `RoleMaxParallelism`, so
one host's declared capacity is never exceeded (AGT-2848). The advisor's input
also includes the freshest review-role plane budget: role `cpu.max`, host cores,
the unchanged per-worker envelope, rolling review duration, and role
`throttled_usec` share, plus load per core and available memory from the same
host snapshot. The ceiling never exceeds one worker per documented 2-core
minimum. A raise requires queue depth at or above `RaiseQueueDepthThreshold`
(stagnation below it never raises), a fresh plane budget, an elapsed raise
cooldown (start-up counts as a change), and no saturation signal: high role
throttling, high load per core, low available memory, or a rolling duration
regression against the duration at the baseline. `RaiseOnStagnation: false`
also holds a stagnant queue at the threshold. Saturation lowers a
recommendation above the configured baseline one step per lower cooldown, even
with a backlog. The decision reason, including host saturation, is returned by
`GET /api/runner/auto-review-parallelism-recommendation`. The runner
applies the same role-quota clamp locally before claiming. Queue input is the
combined backlog of the legacy post-processing queue and current
attempt-authority ReviewAttempts in state Pending (`GET
/api/runner/auto-review-queue`), and a raised
`AutoReviewQueueAdaptiveParallelism:BaselineParallelism` is adopted on the next
refresh once the queue is non-empty and a fresh plane budget shows no
saturation, without a backend restart. The
operator-facing form of this is in
[docs/operations/remote-hosts.md](../../operations/remote-hosts.md) and
[docs/operations/setup/linux-runner-host.md](../../operations/setup/linux-runner-host.md#how-the-review-ceiling-is-derived).

## Aspect execution within one attempt (AGT-3424)

Remote Review runs one attempt's planned commands in two phases
(`RemoteReviewWorkspace.ExecutePlanAsync`; AGT-3400 Dossier
`docs/review-gate-packages/index.html`, decision PK7, option X-B):

1. **Tool commands** (build, tests, lint and every other non-agent command) run
   one after another, as before. An exception in this phase (for example
   `ToolUnavailable`, `CommandStalled`, `NoCpuProgress`) ends the attempt before
   any agent aspect starts. A product failure of a tool command still lets the
   aspects run, as the serial loop did.
2. **Agent aspects** then run concurrently, at most
   `RUNNER_REVIEW_ASPECT_PARALLELISM` at a time (default `4`, capped at `4`;
   `1` restores the serial loop). Aspects start in plan order. The review
   material is built once before the phase and every aspect only reads it.
   Each aspect keeps its own derived budget (see the aspect budget contract
   above), counted from the moment it starts.

Each aspect records into its own evidence lists. The attempt's lists change
only under one gate, which also serialises the checkpoint writes, so every
`review-progress.json` holds whole aspects. Resume semantics are unchanged:
`CanResumeCommand` skips an aspect whose command evidence and verdict are both
checkpointed, and a resumed attempt reruns only the aspects without a
checkpoint. Command evidence and verdicts are reported in plan order whatever
order the aspects finished in.

An infrastructure failure of one aspect (`AspectTimeout`, `ToolUnavailable`,
`CommandStalled`, a failed checkpoint write) stops the attempt where the serial
loop would have stopped: it cancels the aspects still running, starts no further
aspect, and is the one failure reported. Its evidence carries the tool commands
and every aspect that ran. A result that arrives after the stop is evidence,
not a checkpoint. A `block` or `concerns` verdict stops nothing.

The aspects share the attempt's one review slot. Host admission
(`RoleMaxParallelism`, the per-worker resource envelope, the load gate) is
unchanged and counts attempts, not aspects, so the number of aspect calls and
their tokens per review stay the same; only the provider's concurrent peak per
attempt rises, bounded by the cap.

## Infrastructure retry scheduling

A `ReviewInfra` outcome (`AspectTimeout`, `ToolUnavailable`, `BaselineUnavailable`,
a workspace failure, ...) does not mint its linked retry attempt in the same
instant as the failure report. `AttemptAuthorityService.ScheduleReviewInfrastructureRetry`
records a due time instead: bounded backoff of 1 then 3 minutes, indexed by
how many linked retries the chain has already spent
(`AttemptAuthorityService.ReviewInfrastructureRetryBackoff`). The card timeline
gets a `review_infrastructure_retry_scheduled` entry naming the retry number,
the budget of two, the delay, and the failure reason at schedule time; the
card stays in `4-auto-review` with no successor to claim until the delay
elapses. `ReviewInfrastructureRetryScheduler`, a background service, polls
`AttemptAuthorityService.DueReviewInfrastructureRetries` and mints the
successor once it is due. The successor keeps the failed attempt's immutable
ReviewSubject identity (same Result-SHA, requirements, policy and evidence) but
always carries a plan built fresh from the current project settings
(`ReviewInfrastructureRetryScheduler.CreateFreshSuccessor`, AGT-W57): a frozen
plan kept calling a withdrawn review model for 59 attempts, so a corrected
setting now costs exactly one attempt. This closes the
AGT-2841 gap where a `ReviewInfra` verdict left a card sitting in Auto Review
with no automatic next attempt until an operator issued `POST /move`.

One class is never retried: `ReviewInfra/DiagnosisContractUnsupported`
(AGT-3089). `ReviewReportDiagnosisPolicy.Normalize` sets it only when the worker
did not end with `ReviewInfra` itself, a failed deterministic candidate command
has no `Diagnosis`, and the report has no `clean-repeat` evidence. Only a worker
that predates the AGT-2916 diagnosis contract sends that shape, and it would
send it again on every retry of the unchanged SHA. The card escalates at once
as `environmental`, without passing the cause breaker. The reason names the
executor and `Environment.Worker.WorkerReleaseId` and asks the operator to
upgrade the review host. A current worker's own infrastructure exits
(`CommandStalled`, `NoCpuProgress`, `ToolUnavailable`, `TmpMountTornDown`)
keep `DiagnosisMissing` and the bounded retry.

The successor does not pay again for what already passed. After a `ReviewInfra`
outcome the fresh plan names the failed attempt, and only that attempt, in
`ReviewPlanDto.VerifyReuseFromAttemptIds` (`ReviewVerifyReusePolicy`); a product
failure, an inconclusive or cancelled review, and an integration-branch defect
name none. The plan itself is still
built fresh (AGT-W57): it names source attempts, never results, and only the
commands of the new plan that still match are carried over. On the executor,
`runner/ReviewVerifyResultCache.cs` stores every passed deterministic verify
command (build, test, lint) once per host under
`$RUNNER_REVIEW_WORKDIR/.verify-cache`, keyed like the AGT-2843 baseline cache:
repository, exact Result-SHA, command hash (the command line, its working
directory and baseline mode, the preparation commands before it, and a library
step digest), and the toolchain fingerprint. An entry keeps the exit code, the
verdict, and the complete stdout and stderr. The successor re-attaches a
matching entry as its own candidate evidence instead of executing the command,
and executes everything else. Rules that keep the reuse honest:

- **Only a pass from the direct predecessor.** A failed result is never stored
  and never read, and an entry from an attempt the plan does not name is
  ignored. A different Result-SHA, command line or toolchain is a different key.
  A successor that reuses an entry stores it again under its own attempt id
  (keeping the original time and age), so its own successor can carry it one
  hop further. An older attempt of the chain is never a source: when a command
  executes, its stored entry is dropped before it runs and only a pass writes
  it back, so a command that failed or never finished in the failed attempt
  runs again even if an earlier attempt passed it.
- **Aspects always run again.** Agent aspect calls are never stored; the aspect
  that timed out or failed runs as before.
- **A `--no-build` test keeps its build.** A reused command leaves no output in
  the fresh workspace. The clean repeat already runs each verify command alone,
  except a `dotnet test --no-build` that needs its preceding build
  (`ReviewTargetedRerunPolicy.PrecedingBuild`), so that build executes whenever
  such a test executes.
- **Bounded.** Entries expire after 24 hours (pruned on the first lookup or
  store of an attempt), and an attempt that settles without `ReviewInfra` drops
  every entry of its Result-SHA. A prune that races such a delete on the same
  host skips the vanished directory. The store is per
  host: a successor claimed by another review host finds nothing and runs the
  commands.
- **Reuse is visible.** The verdict summary of a reused command, the Markdown
  grade (`verifyReused`/`verifyReuse` frontmatter plus a `Result` column in the
  command evidence table), and `ReviewProjectionView.Attempts[].VerifyReused`/
  `VerifyReuse` carry the sentence worded once by `ReviewVerifyReuse.Citation`:
  `verify result reused from attempt <id> (<age>)`, where `<id>` is the failed
  attempt and `<age>` the time since the command actually executed. The
  evidence row also carries `VerifyReusedFromAttemptId` and
  `VerifyReusedAgeSeconds`; its start and finish times are this attempt's, so
  the pipeline view shows a zero-length step rather than the earlier run.

Before that per-card retry runs, the fleet-wide cause breaker
(`backend/Features/Runner/CauseBreaker/`, AGT-W57) counts the failure by its
cause fingerprint: failure class (`ReviewInfra/<classification>`), normalised
failure text (card keys, SHAs, times, durations, GUIDs, ports and temp paths
removed) and toolchain context (`agent:<cli>:<model>` for an aspect call,
`tool:<executable>` otherwise; the step id is not part of it). At three attempts
or two distinct cards within 24 hours, all three adjustable per project via
`PUT /api/projects/{project}/cause-breaker`, it raises one cause card through
`FailureInterventionService.RaiseCause` (ledger
`.orchestrator/failure-interventions.json`, fleet store
`.metadata/cause-breakers.json`). Every affected card stays in `4-auto-review`
with a `cause-wait.json` marker, gets no scheduled retry, and is skipped by the
review claim. Its CURRENT line names the hold instead of a review queue
position: "Held behind <key>: <cause>", where the cause is
`CauseBreakerWording.Summary` of the failure class (for example "review cannot
read the test report") and `<key>` links the cause card (AGT-3617). A pending review whose plan calls
an open breaker's model route (any project), or whose project already saw an
open tool-level cause, is held before it is claimed. The breaker closes when
the cause card reaches `6-completed` (`CauseBreakerHostedService`) or a review
of the exact successor attempt released by
`POST /api/cause-breakers/{fingerprint}/probe` passes (probe green); a passing
older review on the same waiting card does not close it. The probe is the
newest waiting delivery (the run behind the card's current review finished
last), not the card that waited longest: that card's delivery is usually the
one that broke the review. An optional body `{ "taskKey": "..." }` names the
card to re-check instead; 409 means no card could be released. The cause
card's detail view reads `GET /api/cause-breakers/by-cause/{causeKey}` (held
cards in probe order, probe state) and offers one primary action, "Re-check
now", that requests the probe. An operator moving a code-free intervention card
to Accepted without an override is the owner's decision that the cause is
resolved: the move endpoint records it as a `deliverable-without-code` claim
(`InterventionOwnerDecisionPolicy`), and the sweep closes the breaker. A card
with commits or a run attempt keeps the ordinary acceptance checks. A card whose existing review is still
pending resumes that attempt; a terminal review gets one freshly planned successor.
The report acknowledgement, evidence projection, and delivery resume each
observe the durable passed verdict, so any of those paths can close the exact
probe. Closure is idempotent and logs `cause-breaker-probe-green` once. The
breaker list projects an open probe's current attempt as `running`, `passed`,
or `failed`. The hosted sweep also closes a passed probe left open by an
earlier process. Disabling the breaker through the project settings endpoint
closes its open records with reason `disabled`; enabling it again begins with
no previously open cause.
If successor creation fails, the card keeps its visible wait
and the hosted sweep retries the release; the breaker closes only after every
waiting card is released. The same holds when its `cause-wait.json` cannot be
deleted: the card stays in the fleet store flagged as released, so the claim
admits its successor, no second successor is planned, and the sweep retries the
delete. Once a breaker has a close reason it holds no new pending reviews. Each
sweep also reconciles the fleet store and the markers: a held card whose marker
write failed gets it rewritten, and a waiting marker no open breaker accounts
for is adopted by the open breaker with the same fingerprint or released like a
closed one. An unreadable store fails the call instead of being replaced by an
empty one; a corrupt store is kept as `cause-breakers.json.corrupt-<time>` and
the sweep releases the cards it parked. A red probe, including a product finding or an exhausted
aspect retry, returns the card to its visible wait and clears the probe
reservation. A later explicit probe creates a new attempt from current settings.
With the breaker disabled for a project, the opt-in
`post-failure-intervention` step keeps its previous first-failure behaviour.

Delivery resume stores its retry count, next attempt time, failure reason, and
observed integration verdict in `logs/remote-delivery-resume-retry.json`.
Repeated failures use exponential backoff and stop after five starts for the
same verdict. An integration-owned wait does not consume that budget. A changed
integration or settlement verdict resets the budget so the reconciler can
advance the card after automated recovery. The card stays in the integrating
phase. After exhaustion the post-processing queue stops re-enqueuing it and
shows the specific stop reason; the integration reconciler still watches it.
An unreadable retry file loses its backoff rather than exhausting a delivery.

Once `AttemptAuthorityService.ReviewInfrastructureRetryBudget` (two) linked
retries have all failed, no further retry is scheduled and the card is parked
in `5e-escalated` with a named reason exactly as before this change - the
budget-exhaustion and repeat-diagnosis behavior in
[`ReviewInfrastructureRepeatPolicy`](../../../orchestration-decisions/ReviewInfrastructureRepeatPolicy.cs)
is unaffected by the scheduling delay.

## Baseline verify result cache

A candidate verify failure is not a verdict by itself: the same command runs
again on the merge-base to separate new failures from failures the integration
branch already had. That second run costs what the first cost - the backend
suite is roughly twenty minutes - and several attempts on one integration
branch resolve the same baseline SHA within the hour, so before AGT-2843 a card
paid for the identical baseline run once per attempt.

`runner/ReviewBaselineResultCache.cs` stores each baseline result once per host
under `$RUNNER_REVIEW_WORKDIR/.baseline-cache`, keyed by the inputs the grade
already records: repository, resolved baseline SHA, verify command line, and a
toolchain fingerprint over the `runtime`, `git`, and per-step executable
identities in `ReviewEnvironmentDto.Toolchain`. An entry holds the parsed
failure list plus the complete stdout and stderr, so a reused result is
re-attached as this attempt's baseline command evidence and cites the same
artefacts a fresh baseline run would have produced.

Three rules keep a hit honest:

- **A hit never replaces a candidate run.** The lookup happens only after the
  candidate command has already failed in this attempt's own workspace, and the
  flake retry still re-runs the candidate.
- **Entries expire.** A result older than 24 hours is dropped and re-executed,
  and every baseline SHA the freshly fetched integration ref no longer contains
  is pruned before the first lookup of an attempt.
- **Only a measured baseline is stored.** A successful `test-failures` command
  with no parsed summary carries exit-status evidence for this attempt, and is
  re-executed rather than reused from the baseline result cache. A nonzero exit
  still needs a parsed failure name for failure-name comparison. An incomplete known
  runner or an unbuilt `--no-build` invocation that fails before tests run
  ends as `ReviewInfra/BaselineUnavailable` and is not cached. Reuse checks
  the same evidence, including for entries written by older workers.
  `exit-status` entries carry their result in the exit code and remain reusable.
- **Reuse is visible.** The verdict summary, the Markdown grade
  (`baselineReused`/`baselineReuse` frontmatter plus a `Baseline` column in the
  command evidence table), and `ReviewProjectionView.Attempts[].BaselineReused`
  all carry the same sentence, worded once by
  `ReviewBaselineReuse.Citation`: `baseline result reused from attempt <id>
  (<age>)`. A baseline this attempt executed itself reads `baseline executed in
  this attempt`.

Operating the cache, including where it lives and how to clear it, is in
[linux-runner-host.md](../../operations/setup/linux-runner-host.md#baseline-verify-result-cache).

## Verdict citation contract

Every semantic aspect sentinel supplies these fields:

```text
[[ASPECT_VERDICT: status=<pass|concerns|block>; summary=<sentence>; evidence_checked=<files or sections>; missing=<exact gap, or none>]]
```

`evidence_checked` names the files, diff sections, tests, or result artifacts
the reviewer used. `missing` names the exact absent file, acceptance item,
branch, or contract for a block; pass and concern verdicts use `none` when there
is no missing gap.

`contracts/TaskServer.Contracts/AspectVerdictMarker.cs` is the parser contract
shared by the runner and backend. It accepts wrapped markers and reads status,
summary, evidence checked, missing, and classification. The last complete
marker wins. Within that marker the first value for a key is canonical;
duplicates are preserved as detail and classified `malformed: duplicate-key`,
so a repeated `summary` cannot silently replace the original conclusion.

`contracts/TaskServer.Contracts/ReviewContracts.cs` owns the shared
`ReviewVerdictCitationPolicy`. A semantic block without both meaningful
`evidence_checked` and `missing` values becomes `concerns` with classification
`block-without-citation`. The standalone Task Server and the monolith-compatible
review endpoint both apply this policy at report admission, so an old or
non-conforming executor cannot bypass it. If no cited block remains, the review
grades as Pass with concerns and the acceptance rail does not reissue or
escalate on the uncited refusal. `orchestrator-engine/CouncilLoop` still records
that verdict in its finding count but explicitly excludes the
`block-without-citation` classification from its blocker count.

Deterministic build and test verdicts are not model opinions. A failed command
needs the diagnosis evidence below before its verdict can block the card.

## Mandatory failure diagnosis (AGT-2916)

A failed deterministic command is diagnosed before review grading. The runner
compares the same command on the integration merge base, executes it again in a
fresh same-host clone without restoring the dependency cache, and reads the
normalized fingerprint's Task Server history for the previous 24 hours. This
order is mandatory even if an older frozen plan did not opt into baseline
comparison. The Task Server requires baseline, clean-repeat, and diagnosis
evidence before it accepts a product failure. Green baseline results can be
reused from the baseline cache; red baselines are reported and never cached as
green. The report keeps both runs apart: the `candidate` verification evidence
is always the first, failed run with its own output, and the fresh clone's run
is recorded separately under phase and workspace role `clean-repeat`. A failure
the clean repeat cleared therefore stays visible to diagnosis and grading.

The review fingerprint (`ReviewFailureFingerprint`) is the step id plus the
parsed failed test names. A command without parseable test names, such as a
build or lint step, uses its diagnostic lines instead: lines naming an error,
failure, or exception. Before comparison, every attempt-local path (candidate
`repository`, `clean-repeat-<hash>-<guid>`, baseline workspace, and runtime
home, temp, and cache directories) becomes a placeholder, and all timestamps
and durations are removed. Output without a diagnostic line falls back to the
exit status. When the merge-base command passes and the candidate command
fails on both its original run and clean repeat with ordinary nonzero exit
codes, the runner classifies the delivery as `product` even if diagnostics
change or no test name can be parsed, unless the same failure fingerprint was
seen on another card in the last 24 hours. That cross-card evidence remains
`environment`, including when a green baseline result came from the cache.
Exit 127, timeout and abnormal termination (including signal exits 128 and
above) remain review infrastructure. A first-run timeout receives a clean
repeat before settlement; a passing repeat does not charge the card. The
blocking verdict carries the command, both SHAs, all three exit codes and a
bounded, secret-scrubbed candidate output tail. The fix-round prompt includes
that verdict.

| Evidence | Diagnosis | Card charge |
|---|---|---|
| Baseline green, candidate and clean repeat both have ordinary nonzero exits | `product` | yes |
| Baseline test result red, clean repeat has only failures named on the baseline | `IntegrationBranchDefect` with the pre-existing names | no |
| Baseline test result red, clean repeat has new failures | `product` with the new and pre-existing names | yes |
| Baseline test command cannot produce a test result | `ReviewInfra/BaselineUnavailable` | no |
| Same fingerprint on another card with a green baseline | `environment` | no |
| Clean repeat green and fingerprint has known intermittent pattern or prior occurrence | `intermittent` | no |
| Clean repeat green without history, or required evidence unavailable | `unclassified-first-occurrence` | no |
| Clean repeat green, baseline red on cached state, clean baseline repeat green | `DependencyCacheDefect` or `BaselineResultCacheDefect` | no |
| Reviewer block without evidence against a changed path | `concern` | no |

**Missing test reports.** A successful test command without a recognized
summary supplies exit-status evidence. A failed test command with no parseable
result is retried once, before saving the dependency cache, and both attempts
are recorded. If it still has no result, it settles as
`ReviewInfra/BaselineUnavailable` and names the expected report path (or the
captured stdout artifact for console-only reporters). An explicit missing-file
diagnostic from the frontend wrapper follows the same path even when the wrapper
exits nonzero. If its retry still has no result, it settles as
`ReviewInfra/TestReportUnavailable`, unless the delivery changed test
configuration, the runner or reporter, or added, removed, or renamed a spec. In
that case the missing report is a product finding that cites the changed setup
path. A report retry with a missing declared dependency directory settles as
`ReviewInfra/DependencyDirectoryMissing` and names the review harness defect;
it does not charge the card. The result parser accepts Vitest's
`Test Files N passed` summary as well
as its `Tests N passed` summary. If Angular finished bundling but Vitest did not
report a run, the infrastructure summary says so and points to the captured
stdout artifact. The frontend `test:ci` command writes a JSON report to a
unique temporary file for each run. Its wrapper waits for Angular to exit,
checks that the file contains completed tests, and prints a parseable summary.
If Angular exits without the file, the wrapper names its path and exits nonzero;
review still applies the missing-report retry and classification rule. The
generated spec basename is below the filesystem filename limit; the former
frontend command configured only a console reporter, so it had no independent
report to inspect when that console summary was absent.

**Red baseline on cached state (AGT-3430).** A baseline that ran on a
dependency-cache hit, or a baseline result reused from the baseline result
cache, does not prove the integration branch red while the candidate's clean
repeat of the same command is green. Before the card or the branch is blamed,
the runner runs that baseline once more in a fresh same-host clone with fresh
home, temp and cache directories and without the dependency cache
(`ReviewCachedBaselinePolicy`). Its evidence has phase `clean-repeat` and
workspace role `baseline-clean-repeat`. Green settles `ReviewInfra` with
`DependencyCacheDefect` (a dependency scope of the baseline's preparation was
restored from the cache, whether the command was then skipped or, since
AGT-3429, reran over the restored directories) or
`BaselineResultCacheDefect` (the red result was reused), records the clean
baseline's exit code 0 as the candidate's `BaselineExitCode`, evicts the stored
baseline result, the baseline dependency-cache entry and the candidate entry,
and does not charge the card. Red keeps the measured baseline's attribution
(`IntegrationBranchDefect` for a build or a test with no new failures). A clean
baseline that times out, is killed, or runs no test settles
`ReviewInfra/BaselineUnavailable`. At most one such repeat runs per attempt;
the recorded evidence carries the bound across a resumed worker.
`ReviewReportDiagnosisPolicy` accepts a cache classification only with a green
`clean-repeat` and a green `baseline-clean-repeat` for the same step and
baseline, otherwise `DiagnosisEvidenceInvalid`.

**Failure text and output tail (AGT-3430).** The summary of a test-failures
baseline that exited without a test result quotes the lines that explain the
failure, from stdout and stderr: lines naming an error, `FATAL`, a failure or an
exception come first, npm's own `npm error` wrapper lines after them, and known
noise (`npm notice`, `npm warn`) is skipped. Without such a line the first other
stderr line, else stdout line, is quoted. At most three lines of 240 characters,
secrets redacted. Every failed command's evidence row also carries
`OutputTail`: the last 4096 characters
(`ReviewCommandOutputTailDto.MaximumCharacters`) of stdout and of stderr, each
with a truncation flag and redacted like the product-failure evidence tail, so
the cause survives the deleted attempt in the settled report.

The shared decision table is `DeliveryFailureDiagnosis`. Review and gate
reporters append observations to the Task Server's fingerprint event store via
the runner API. The cause breaker can read the same 24-hour summaries; a
management endpoint exposes the store without a new UI. Only confirmed
`product` diagnoses spend the retry budget, anti-churn limit, and reissue
counter. `environment` evacuates the participating dependency cache entry and
frozen review state. An unavailable fingerprint store cannot establish a
card-specific failure from one run. The green-baseline and repeated-red
exit-status proof is independent of that store.

The older `ReviewFailureAttributionPolicy` still explains pre-AGT-2916
reports, but does not authorize a new card charge. For evidence that carries a
diagnosis and a measured baseline it defers to the diagnosis: only `product`
is owned by the delivery, a red baseline by the integration branch, anything
else is tolerated. A red baseline now yields
`ReviewInfra/environment` even if a failure name appears new against it. A
semantic reviewer block needs a cited changed path and carries an explicit
`product` diagnosis with confidence and citation; uncited blocks settle as
`concern` diagnoses. Missing baseline or clean-repeat evidence settles as review
infrastructure, with no product charge.

## Merge-gate failure triage (AGT-3009)

A red merge gate used to park its card with an untyped reason, which the board
reads as `parkedBlocker.blockerType = operator-decision`. An operator script
then read the gate log and queued a redelivery or a fix round. The product now
makes that call itself before the card may park.

`DeliveryChainReconciler` hands every `build-gate-failed`,
`gate-environment-failure`, and `gate-interrupted` card to `GateFailureRouter`
(`backend/Features/Pipeline/GateFailureTriage/`). The router reads the newest
`post-steps/pre-develop-build-gate-N.log` (or `pre-main-test-gate-N.log`) and
the pure `GateFailureTriagePolicy` assigns one class:

| Class | Decided by | Route |
|---|---|---|
| `environment` | `Worker exited unexpectedly`, `JavaScript heap out of memory`, `violated gate-run budget` / `budget=gate-run`, `ECONNRESET`, `ETIMEDOUT`, `unable to access`, `Could not resolve host`, a gate-typed toolchain failure, an interrupted gate, or an AGT-2916 clean repeat that passed on the same tree | Transport retries the same exact-subject gate at most three times. Other environment failures stay in Auto Review while the bounded gate-environment ladder replays integration with the passed review |
| `product` | Failing test or build-error names, no environment marker | One fix round per delivery SHA, whose prompt carries the failing items and the reason line |
| `integration-branch` | AGT-2916 diagnosis `baseline=red` | Opens or joins the cause card for the fingerprint; the card waits on it |
| `undecidable` | No gate log, a green newest log, conflicting markers (an environment marker and failing items), or a flake label without a re-run | Parks with `[gate-undecidable]`, stating the missing evidence |

A merge whose gate ran no check because the repository defines no verify
commands never reaches this table: it carries `verify-definition-missing`
(AGT-3428), which the router does not handle, and parks with the message that
names the missing `.agent-studio/project.yml`. Legacy `build-gate-failed`
records whose reason is `no verify commands derivable` are re-typed the same way.

The diagnosis suffix of AGT-2916 wins over text markers because it is an
experiment. The router re-types the durable merge step to the decided class,
so the replay ladder and the board agree with the triage.

**Fleet-wide fingerprint.** For a class with failing items, the fingerprint
depends only on the sorted item names. The router records each observation in
the Task Server failure-fingerprint store (source `gate-triage`), the same
counter the AGT-2917 cause breaker reads. When a product fingerprint has
already failed on another card within seven days, the card does not get its
own fix round: `FailureInterventionService` opens or joins one cause card
(`gate/shared-cause`), records it in the card's `BlockedBy`, and the card parks
with `[gate-shared-cause] ... Waiting on <cause>`. An unreadable counter never
creates a shared cause.

**No silent flake label.** A gate may call a failure flaky only when it re-ran
the same item on the same tree and the re-run passed (`retryPerformed=true`).
A flake label without that re-run is `undecidable`, and its items are still
fingerprinted and counted. Review report admission uses
`ReviewFlakeEvidencePolicy` through the shared diagnosis policy on both the
Task Server and monolith paths. A retry flag or an item disappearing from
failed retry output is insufficient: the evidence must include a successful
clean repeat on the same commit and tree. Its evidence retains the frozen plan
arguments. A class-only repeat declares the added filter and the original
failed test names observed passing; both report authorities check them. Unsupported
command or reviewer flake claims become `unclassified-first-occurrence`
(the diagnosis contract's undecidable result), with the missing proof stated.
Named items from unsupported command claims enter the existing fingerprint
store under the same item identity as gate triage. The review executor only
quarantines marked items after a successful repeat; its original failed
observation remains in the existing diagnosis history.

**Fallbacks.** Each automatic route is bounded, and when it runs out the card
parks with a typed category rather than `operator-decision`:
`gate-environment-exhausted` (ladder spent, or automatic continuations
disabled) and `gate-product` (fix round spent, disabled, or unable to start).
Only `gate-undecidable` asks a person to judge the failure. The gate itself is
unchanged, and a failed delivery is never accepted. The last routing per gate
log is kept in `post-steps/gate-triage.json`, so a reconciler tick repeats a
side effect at most once per log. Cause-card creation must succeed before an
`AttachToCause` receipt is saved. A failed creation leaves the card in place
for the next reconciler sweep, and a legacy receipt without a cause key is
retried rather than treated as completed.

Transport failures are retried by `RemoteBuildTestGateRunner` at most three
times against the identical gate request and exact commit, before the delivery
router sees a terminal result. A failure naming product test items is not
retried merely because its output also contains network text. Transport
exhaustion is excluded from integration replay and retains a typed environment
blocker; it never starts an agent fix round or accepts the failed gate.

## Integration-branch gate health (AGT-2819)

Because each gate is measured on the merge base on every delivery, the review
plane is also the integration branch's health monitor.
`backend/Features/Pipeline/IntegrationBranchGateReporter.cs` reads each settled
report, and `IntegrationBranchGateHealthPolicy` decides what is news:

- `TurnedRed` is the only alerting transition, so a branch that stays broken for
  weeks costs one operator-feed message (`topic: integration-branch-gate-red`),
  not one per card that passes through it.
- The alert names the step, its command, the merge-base commit the gate is red
  at, and - when an earlier green measurement exists - the
  `<last green>..<red>` range the breaking commit lies in. It never claims a
  single culprit commit, because a merge-base measurement cannot prove one
  without a bisect.
- Only a merge-base run moves the named commits. A gate that simply passed on a
  delivery clears a stale red flag without recording a branch commit, so a later
  regression alerts again.

State lives in
`<TaskRepository>/logs/integration-gate-health/<project>.json`. Losing that file
costs one repeated alert, never a missed one.

## Worker release provenance (AGT-2863)

A review daemon restart adopts the running detached workers instead of killing
them (AGT-2753 / AGT-2836), so the process that grades an adopted attempt is the
**previous** release's `agent-host --detached-review-worker`, while the daemon
that reports the verdict is the new one. Before this contract, the evidence
bundle recorded only executor and fence, so a verdict graded by a superseded
build was indistinguishable from one graded by the current release.

Every review attempt now records who graded it:

- The launching daemon stamps `WorkerReleaseId` and `WorkerBinaryPath` into its
  `RUNNER_STATE_DIR/reviews/<attempt>.review-slot.json` record, and the worker
  corroborates both in `review-worker.json`. The binary path is resolved through
  the `current` symlink, so it names `/opt/agent-host/releases/<id>/agent-host`
  even after a promotion moved that link.
- The report carries `ReviewEnvironmentDto.Worker`
  (`ReviewWorkerProvenanceDto`: worker release, worker binary, daemon release).
  The field is optional on the wire; a report from a runner that predates it is
  accepted unchanged.
- `ReviewWorkerProvenancePolicy` is the single reading of that record. A release
  is only compared when both sides are known, so a slot adopted from a
  pre-AGT-2863 daemon reads `unknown` and never produces a false mismatch.
- The grade file writes `workerReleaseId`, `daemonReleaseId`, and (on a
  mismatch) `workerReleaseSuperseded` into its frontmatter, and
  `TaskScopedTestEvidenceReader` reads them back into
  `ReviewAttempt.WorkerReleaseId` / `DaemonReleaseId` /
  `WorkerReleaseSuperseded`, so every review surface reads one projection
  rather than re-parsing the report.

Two restart modes use that record, and **neither ever ends an adopted worker**:

| Mode | Selected by | Behaviour |
|---|---|---|
| Adopt and report (default) | nothing to set | The replacement finishes every adopted attempt and claims beside it. One `review worker release superseded ...` journal line per mismatched attempt, the release pair in the grade's *Immutable subject proof* block, and one `review_graded_by_superseded_release` timeline entry on the card. |
| Release drain (opt-in) | `agent-runner-deploy --restart-review-drain`, or `RUNNER_REVIEW_RELEASE_DRAIN=1` on an already restarted daemon | The deploy form drains Review before the unchanged promote step. The daemon knob still finishes every adopted attempt but closes claim admission (`ReviewReleaseDrainPolicy`) while a superseded worker runs, so a worker-side hotfix reaches every attempt this daemon grades. |

The drain decision reuses `ReviewSlotAdmissionDecision` with its
`ActiveSlotsContinue` default, which is what makes "hold claims" structurally
incapable of cancelling a running review. Operator procedure and the log lines
to check live in
[linux-runner-host.md](../../operations/setup/linux-runner-host.md#which-release-grades-an-adopted-review).

## Review host restarts stay visible (AGT-3690)

A replacement daemon that cannot prove a review worker alive settles the
attempt as `ReviewInfra / ExecutorRestarted` and the server counts it as a
`worker-not-reported` loss. Three surfaces say so:

- **Card.** The Auto Review queue line reads the failure classification, the
  lease host, the loss time and the scheduled retry from the board snapshot:
  "Review lost: review host restarted (lab-review-host, 02:27). Automatic
  retry 1 of 3 at 02:28." The host name links to the host detail.
- **Host detail.** `AttemptAuthorityService` keeps the newest 20 restarts per
  review runner identity, each with its lost attempts and their task keys.
  `GET /api/v1/management/remote-hosts` returns them as `reviewRestarts`, and
  the host detail lists them with a link to each lost card. `restartedAt` and
  `reviewsLost` still describe the newest restart only. The standalone Task
  Server keeps only that newest restart for now.
- **Escalation.** When the retry budget is spent and the newest cause is a
  restart, the card escalates as `environmental` and the reason names every
  restart (host, time, attempt) instead of "cannot be materialized".

Covered by `TaskLiveStatusProjectionTests`, `ReviewAttemptChainSummaryTests`,
`V1ReviewPlaneDiagnosticsEndpointTests.Restart_history_keeps_a_lost_review_after_a_later_clean_restart`,
`RemoteRunnerEndToEndTests.Monolith_v1_review_host_restart_names_the_host_and_retry_then_escalates_on_the_restarts`,
and `frontend/e2e/settings/first-run-review-host-restart.spec.ts`, which kills
the real `first-run-review` daemon mid-review twice.

## Terminal slot deletion is final (AGT-2864)

One `RemoteReviewExecutor` drives one review slot, and two writers touch its
`RUNNER_STATE_DIR/reviews/<attempt>.review-slot.json` record: the execution path
(phase transitions, the pending re-claim journal) and the heartbeat, which
persists every renewed lease so a reconciliation pass reads a live expiry.

The heartbeat runs until `RunPersistedAsync` returns, which is after the
terminal report, the workspace cleanup, the cleanup acknowledgement, and the
delete of the slot record. A renewal the Task Server has already answered
persists that answer whenever its continuation is scheduled, and that write
deliberately has no cancellation check: the answer is authoritative the moment
it arrives. On a busy host the continuation can be scheduled after the delete,
and it then wrote the record back. The resurrected slot named the fence of an
attempt that was already reported and cleaned up, so the next daemon generation
adopted it, verified authority against a settled attempt, and reported it a
second time.

`ReapSlot` closes that window. It marks the slot reaped under the same
`_slotGate` every persisting write holds and then deletes the record; the
writers go through `Write`, which keeps this executor's in-memory view moving
but no longer touches the store once the slot is reaped. The order is decided by
the gate rather than by the thread pool: a renewal that takes the gate first
writes a record the delete then removes, and one that takes it afterwards writes
nothing. Nothing else about AGT-2753 adoption changes, because every write
before the reap behaves exactly as before.

The margin was never large. In `ReviewHandoffAdoptionTests`, which drives this
scenario with a one second heartbeat, the delete lands 285 ms before the first
heartbeat tick when the class runs alone, and 131 ms before it inside the full
parallel `AgentRunner.Tests` run. On a host with a large process table the tick
moves inside the cleanup window altogether and its write precedes the delete by
as little as a millisecond, because the pre-delete segment is dominated by
`/proc` scans in the workspace reaper. From there it is the thread pool, not the
product, that decides which write reaches the record last; the promotion gate on
17.09.2026 is where it went the other way.
`RemoteReviewExecutorTests.Renewal_persisted_after_terminal_cleanup_leaves_the_slot_reaped`
pins the interleaving without depending on that timing.

## Runner and Task Server gates in the review plan (AGT-2864)

The frozen plan's deterministic gates are derived from
`.agent-studio/project.yml` `commands.test` by `VerifyCommandPlanner`. The .NET
part of that list was `backend.Tests` alone, so a delivery under `runner/` or
`task-server/` was reviewed without its own suite ever running: a runner-side
race could only surface in the Windows gate or in the develop to main promotion
gate, which is one full lane after review had already passed it. The review gate
now runs both suites as well:

| Gate | Command |
|---|---|
| verify-4 | `dotnet test runner.Tests/AgentRunner.Tests.csproj --no-build --filter Category!=MachineBound` |
| verify-5 | `dotnet test task-server.Tests/TaskServer.Tests.csproj --no-build --filter Category!=MachineBound` |

The filter is the one the backend gate already uses, so the machine-bound
topology and daemon-recovery suites stay off the review host. On a 12 core host
the two add roughly one and two minutes to a review. The historical 691-case
Runner sample completed in 62 s with an older command line; it is not a timing
measurement for the current `ReviewPlanResourcePolicy` command, which includes
TRX logging, a 10-minute hang timeout and no hang dump. xUnit collection
parallelism therefore stays on for these gates, which is what makes them able to
see a race like AGT-2864 at all. A fixed two-thread xUnit cap was measured and
removed: under ten concurrent test processes it made the 1,298-test Runner
suite 100 seconds slower, and under six concurrent test processes it made four
backend endpoint tests 8.5 seconds slower. No xUnit config is retained until
per-suite and admission measurements justify one. The former
`-p:ParallelizeTestCollections=false` MSBuild property was inert and has been
removed. A test that makes no progress for ten minutes now triggers VSTest's
hang diagnostics.

Every planned .NET review test command now writes both verbose console output
and TRX. The review runner retains a compressed TRX and a compact JSON timing
summary as review artifacts. The summary records test count, summed and wall
time, per-class totals, the slowest 50 tests, retry status, and host load and
active test-process count sampled when the report is captured. A missing or
malformed TRX is logged and does not change the test verdict.

For a delivery that changes `backend/`, the review plan inserts the declared
.NET guard commands before its selected tests. These use
`FullyQualifiedName~.Architecture.|Category=Guard` together with each command's
existing filter. The guard commands come from the project inventory, so class
selection cannot omit them, including when it selects no other test class.
The route inventory's three-profile permission census and the
`TaskFolderAccessIsolationTest` class carry the Guard category because their
namespaces do not match the architecture filter.

With full review testing, both suites run for every review of this repository,
including deliveries outside `runner/` and `task-server/`. A review plan is
frozen before the attempt is claimed and is immutable by contract. The later
per-card and batch review selectors use the delivery diff to narrow the
remaining test commands; the backend guard step above stays in the plan.

When a .NET review test step is red, the clean repeat now keeps the original
category filter and reruns only the classes that reported failures, up to five
classes and ten failed tests. An aborted or crashed run, an unparseable failure,
or a chained command keeps the full clean repeat. A successful class subset
that does not report every previously failed test triggers a full repeat; a
green exit without those tests is not flake proof. The original full-suite
failure remains in the review evidence and the baseline comparison still
determines attribution. For a planned `--no-build` test, fresh baseline
worktrees and clean-repeat clones replay the preceding frozen build after
dependency preparation so the test assembly exists.
A green clean repeat must show that the failed tests ran (AGT-3650). `dotnet
test` names a passed test only at normal console verbosity, so the repeat adds
`--logger "console;verbosity=normal"` unless the command already chose a
console logger. Vitest and Jest failures are named `file > suite > test`. The
repeat sets `AGENT_STUDIO_VITEST_REPORTER=verbose`, which `frontend/vitest.config.ts`
reads to select the verbose reporter. That reporter names every passing test.
A passed line for each failed test is `test-names` proof. If the output names
no tests but every failed test's file is reported passed, as with Vitest's
default reporter or Jest's `PASS` lines, the proof is `test-files`. The level
is recorded in the clean repeat's `RepeatObservation`. A green repeat with
neither proof stays `ReviewInfra / CleanRepeatUnavailable`.
When the clean repeat has no new failures against a baseline that reported
failed tests, the verification aspect passes and names the pre-existing
failures in its verdict summary and card review evidence. The review reaches
`IntegrationBranchDefect`; the batch gate remains the authority for the full
suite. New failures reproduced on the candidate charge the card even when
the baseline was red.

For batch-deferred reviews, `RemoteReviewPlanBuilder` compares the delivered
test project with its merge base before treating a change to
`backend.Tests/OrchestratorApi.Tests.csproj` as a verify command inventory edit.
Adding or removing only `None`, `Content`, or `EmbeddedResource` items with
optional copy metadata keeps the build-only review and leaves the full suite
to the batch gate. Package and project references, compile items, target
frameworks, and test discovery properties still disable deferral. Changes to
`.agent-studio/project.yml`, `frontend/package.json`, `frontend/angular.json`,
or `backend/OrchestratorApi.csproj` also disable it. The frozen plan records
the rule that disabled deferral, and the card's review tool gate cites it.

## No-change results skip the tool gates (AGT-3079)

A result that changes no path against its merge base with the integration ref
(for example a `noop` or a commit-less `done` whose Result-SHA is already on
`develop`) would only compare that SHA with itself in build, test and lint.
`RemoteReviewPlanBuilder.IsNoChangeResult` decides this from a computed, empty
changed-path list, a valid Result-SHA and a present result ref. A null list
(the diff could not be computed) keeps the full plan. Such a plan then keeps
its agent aspects, drops every `build-tests`, `lint`, `review-verification`
and compose-render command and all dependency preparation, and records each
dropped tool aspect under `SkippedAspects` with a reason that names the
integration ref. A plan without agent aspects keeps the `verify-subject`
completion command, so the frozen plan is never empty. The batch deferral does
not apply, and delivery admission reads the plan as having no applicable
build/test gate. The integration lane still verifies the contained tree before
completion (AGT-3002).

The empty list must be measured against the integration ref. The completion
path diffs against the run's pickup base, which a continuation round sets to
the previous, still unintegrated result; a round without a commit then has an
empty pickup diff while its delivery still changes code.
`RemoteReviewPlanBuilder.CompletionChangedFiles` therefore measures an empty
pickup diff again against the integration ref and keeps any non-empty pickup
diff unchanged. The review retry scheduler and the settings-edit replan diff
the Result-SHA against the integration ref directly, so all three freeze the
same no-change decision. The replan no longer diffs the result ref, which
usually exists only under `refs/remotes/origin` in the Studio checkout.

## Key code and tests

- `runner/RemoteReviewWorkspace.cs`: exact checkout, integration-ref fetch,
  merge-base, bounded review material, aspect parsing, executor-side citation
  downgrade, and the per-attempt process environment.
- `runner/ReviewAspectInputBudget.cs` and `runner/ReviewCliRefusalPolicy.cs`:
  final aspect input limits and pre-model CLI refusal attribution, covered by
  `runner.Tests/ReviewAspectInputBudgetTests.cs` and
  `runner.Tests/RemoteReviewAspectParallelismTests.cs`.
- `runner/RemoteReviewExecutor.cs` (`Write`, `ReapSlot`): the single durable
  slot write and the terminal reap that a late heartbeat renewal cannot undo,
  covered by
  `runner.Tests/RemoteReviewExecutorTests.cs`
  (`Renewal_persisted_after_terminal_cleanup_leaves_the_slot_reaped`).
- `runner/ReviewBaselineResultCache.cs`: baseline result key, bounded lifetime,
  integration-branch pruning, and the atomic per-entry store.
- `runner/ReviewBuildServerIsolation.cs` and
  `runner/CommandProgressWatchdog.cs`: the per-attempt build namespace and the
  CPU-progress hang watchdog.
- `backend/Features/Runner/RemoteReviewPlanBuilder.cs`: freezes task context,
  aspect templates, CLI, model, thinking level, and integration ref.
- `contracts/TaskServer.Contracts/ReviewContracts.cs`: verdict wire shape and
  shared citation policy.
- `task-server/TaskServerReviewStore.cs` and
  `backend/Features/Runner/V1ReviewPlaneEndpoints.cs`: authoritative report
  admission and acceptance-rail protection.
- The rail's integration truth and its one-refusal-per-state-change rule live
  in the pipeline domain:
  [pipeline.md](./pipeline.md#acceptance-rail-one-integration-truth-one-refusal).
- `orchestrator-engine/OrchestrationStageHandlers.cs`: Council records an
  uncited downgrade as a concern without turning it back into a reissue.
- `contracts/TaskServer.Contracts/ReviewPackageContracts.cs`,
  `orchestration-decisions/ReviewPackageFormationPolicy.cs`,
  `backend/Features/Review/Packages/`, `task-server/TaskServerReviewPackageStore.cs`
  and `runner/PackageReview*.cs`: shadow package review (AGT-3400 S4), covered by
  `backend.Tests/ReviewPackageFormationPolicyTests.cs`,
  `backend.Tests/ReviewPackageContractTests.cs`,
  `backend.Tests/ReviewPackageFormationServiceTests.cs`,
  `task-server.Tests/ReviewPackageStoreTests.cs` and
  `runner.Tests/PackageReviewExecutorTests.cs`.
- `runner.Tests/RemoteReviewWorkspaceTests.cs`: exact docs-diff visibility and
  explicit truncation coverage.
- `runner.Tests/ReviewConcurrentWorkspaceIsolationTests.cs`: four concurrent
  attempts of one subject, and the reap-and-classify path for a command blocked
  on a host-shared handle.
- `runner.Tests/ReviewBuildServerIsolationTests.cs` and
  `runner.Tests/CommandProgressWatchdogTests.cs`: the two decision matrices.
- `scripts/review-build-server-isolation-probe.sh`: operator probe that measures
  host-global build servers left behind by concurrent attempts.
- `backend.Tests/ReviewGradingPolicyTests.cs`: uncited-block downgrade and cited
  block preservation.
- `backend/Features/Review/ReviewFindingDataBlock.cs` and
  `backend.Tests/ReviewFindingDataBoundaryTests.cs`: the nonce-fenced data
  boundary for reviewer findings, tested with instruction-like finding text.
- `backend/Features/Runner/AttemptAuthorityService.cs`
  (`ScheduleReviewInfrastructureRetry`, `DueReviewInfrastructureRetries`,
  `ClearScheduledReviewInfrastructureRetry`) and
  `backend/Features/Runner/ReviewInfrastructureRetryScheduler.cs`: bounded
  backoff scheduling and firing of linked `ReviewInfra` retries.
- `backend.Tests/AttemptAuthorityServiceTests.cs`
  (`Schedule_review_infrastructure_retry_defers_the_successor_by_one_backoff_step`,
  `Schedule_review_infrastructure_retry_uses_widening_backoff_and_stops_at_the_cap`)
  and `backend.Tests/RemoteRunnerEndToEndTests.cs`
  (`Monolith_v1_review_plane_schedules_a_named_retry_for_a_fake_aspect_timeout_without_an_operator_move`):
  the backoff schedule, the two-retry cap, and the
  `review_infrastructure_retry_scheduled` timeline entry.
- `backend/Features/Runner/ReviewVerifyReusePolicy.cs`,
  `runner/ReviewVerifyResultCache.cs`, `backend.Tests/ReviewVerifyReusePolicyTests.cs`
  and `runner.Tests/ReviewVerifyResultReuseTests.cs`: which failed attempts a
  `ReviewInfra` successor may take passed verify results from, the per-host
  store, and the fake-aspect-timeout reproduction in which only the failed or
  unrun commands execute again.
- `contracts/TaskServer.Contracts/ReviewAspectBudgetPolicy.cs` and
  `runner.Tests/ReviewAspectBudgetPolicyTests.cs`: the derived per-aspect budget.
- `contracts/TaskServer.Contracts/ReviewPlanResourcePolicy.cs` and
  `runner.Tests/ReviewPlanResourcePolicyTests.cs`: CPU cap and node-reuse switch
  for every .NET command in a frozen plan.
- `backend/Features/Runner/AdaptiveReviewParallelismPolicy.cs` and
  `backend.Tests/V1ReviewPlaneDiagnosticsEndpointTests.cs`: the recommendation
  and the advertisement that delivers it to the review runner.
- `backend/Features/Runner/RemoteReviewReportEvidence.cs` and
  `backend/Features/Review/ReviewProjection.cs`: the grade's baseline-reuse
  citation and the card projection that reads it back, covered by
  `backend.Tests/RemoteReviewReportEvidenceTests.cs`.
- `runner.Tests/RemoteReviewWorkspaceTests.cs`: baseline cache hit, miss,
  expiry, pruning, and a new-failure classification against a reused baseline.
- `contracts/TaskServer.Contracts/ReviewFailureAttributionPolicy.cs` and
  `backend.Tests/ReviewFailureAttributionPolicyTests.cs`: the attribution matrix.
- `backend/Features/TestRuns/TaskScopedTestEvidenceReader.cs` and
  `backend/Features/Review/ReviewProjection.cs`: the grade's release provenance
  read back onto the review projection, covered by
  `backend.Tests/RemoteReviewReportEvidenceTests.cs`.
- `runner/RunnerReleaseIdentity.cs`, `runner/DurableReviewProcess.cs`,
  `runner/ReviewReleaseDrainPolicy.cs`, and
  `contracts/TaskServer.Contracts/ReviewContracts.cs`
  (`ReviewWorkerProvenanceDto`, `ReviewWorkerProvenancePolicy`): worker release
  provenance and the two restart modes, covered by
  `runner.Tests/ReviewWorkerReleaseProvenanceTests.cs`,
  `runner.Tests/RunnerReleaseIdentityTests.cs`, and
  `backend.Tests/RemoteReviewWorkerReleaseProjectionTests.cs`.
- `runner/ReviewInfraAttributionPolicy.cs` and
  `runner.Tests/ReviewInfraAttributionPolicyTests.cs`: the separate,
  runner-local decision of whether a failed command is a torn-down-`/tmp`
  incident or product-owned, from that command's own output.
- `backend/Features/Pipeline/IntegrationBranchGateHealthPolicy.cs`,
  `IntegrationBranchGateHealthStore.cs`, `IntegrationBranchGateReporter.cs` with
  `backend.Tests/IntegrationBranchGateHealthPolicyTests.cs` and
  `backend.Tests/IntegrationBranchGateReporterTests.cs`: red-transition alerting.
- `runner.Tests/RemoteReviewWorkspaceTests.cs` and
  `task-server.Tests/RemoteReviewAuthorityTests.cs`: a lint gate red on the merge
  base settles as `IntegrationBranchDefect`; red only on the delivery still
  settles as `ProductFailure`.
- Isolated baseline worktrees and clean-repeat clones replay the frozen build
  before a `--no-build` test. A compiler error is recorded under the build
  step with its output. A red baseline build blocks the dependent test's
  comparison and retains `IntegrationBranchDefect`; a red clean-repeat build
  preserves the build fingerprint, so a delivery compile error remains
  `ProductFailure`. Missing build steps, toolchains, or interrupted processes
  remain review infrastructure failures. The shared report diagnosis policy
  and standalone Task Server accept a failed prerequisite build only when it
  matches the planned preceding step and baseline SHA.
# Review observations and later evidence (AGT-3269)

Studio projects executed aspect and grade steps from each task's durable
`pipeline-execution.json`, including prior attempts and repeated step runs, into
review observations. Each observation keeps the recorded model, thinking level,
four token dimensions, duration, verdict, severity counts, historical list-price
estimate and evidence reference. New local and remote aspect projections, grades,
and product gate projections bind the exact delivered Result-SHA. Older rows without that SHA remain
visible for cost analysis but cannot receive an automatic miss attribution.

`POST /api/tasks/{jobId}/review-observations?project=...` records an externally
performed deep review (or a review outside the pipeline) in the task-local
`results/review-observations.jsonl` journal. The request includes its exact SHA,
measurements and cited artifact. When its cost is omitted, Studio uses the
historical price catalogue at the review date if the model is priced.
`POST /api/tasks/{jobId}/review-observations/{reviewId}/later-evidence?project=...`
links a later review, gate, operator finding or production incident to an earlier
approved review of the same SHA. The later evidence must be dated after approval
and cite an artifact. A later blocking review of the same aspect and SHA supplies
the link automatically. Replayed journal IDs do not duplicate
records.

`POST /api/tasks/{jobId}/review-observations/{reviewId}/finding-outcomes?project=...`
records a cited confirmation and dismissal count for findings reported by a
review. The journal keeps each assessment; the latest one supplies cost per
real finding and the optional Token Economy `outcomes` field.

`GET /api/projects/{projectName}/token-usage/review-observations` returns the
per-review records, later evidence links and model/reasoning aggregates. The
`/export` suffix returns a dated manifest with its method and
`studio-observation` status. Eligible `runs` use Token Economy's
`quality-studio-review-run-v1` drop shape; rows without a measured positive
file count, CLI, model or reasoning level appear under `excludedObservations`
instead of receiving invented values. The export does not claim publication in
Token Economy. `/export/{reviewId}` returns one importer-ready version-1
review-run JSON document when that review has the required fields.
Unpriced reviews retain null cost. Cost per real finding is null unless reported
findings have an explicit confirmed count. When a model/reasoning cohort has at
least twenty approvals and four cited misses with a miss rate of at least 20%, the
remote review plan runs every semantic aspect on a follow-up round instead of
carrying previous verdicts over. Failed commands do not count as reviewed defects;
an automatic later-review miss applies only to an earlier approval of the same
aspect and delivered SHA. The previous finding remains in the full-review prompt.
This affects review depth only, not the task's
acceptance or transition decision.

## Quality Studio change ranges on cards

The Review Plane workspace proof supplies the reviewed merge base and immutable
Result SHA. The task keeps one `results/quality-studio/review-<attempt>.json`
artifact per review attempt with that range, delivery generation, run attempt,
commit count excluding superseded commits, mode, and an optional
`change-review-verdict.v1` document. The Remote Review grade file also records
the merge base. This preserves an older round when the integration ref moves.

`GET /api/tasks/{id}/quality-studio` projects those artifacts for the Evidence
tab. It compares the reviewed patch with a no-ff merge's first-parent range
using `git patch-id --stable`. A missing object leaves patch equality unknown.
The panel and its signed hand-off range link make only read requests. The
Review Plane supplies the document as a digest-checked report artifact named
`change-review-verdict.v1.json`; its producer controls the off, shadow and decide
mode. An absent artifact does not become a pass. The durable outbox sends a
present document to Quality Studio's versioned `PUT .../change-reviews` route,
replays the same key across restarts, and emits an operator-feed event after
seven days without delivery.
