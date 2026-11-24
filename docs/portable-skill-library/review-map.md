# Review map

Canonical Dossier: [Portable skill library](index.html). Source card: AGT-4032.
As of 24 November 2026. This map addresses the reviewed finding that required
sections and the full implementation-card sequence could not be verified.
It identifies the delivered content; it does not record sight review, decision
acceptance, implementation or operator approval.

## Required sections

| Contract marker | Section |
| --- | --- |
| `data-concept-section="alternatives"` | [Alternatives](index.html#alternatives): four options with tradeoffs. |
| `data-concept-section="recommendation"` | [Recommendation](index.html#recommendation): managed prompt bundles on the existing enrichment seam and continuity. |
| `data-concept-section="evidence"` | [Evidence](index.html#evidence): source inspections, owner input and explicit limitations. |
| `data-concept-section="open-decisions"` | [Open decisions](index.html#open-decisions): SL-D1 through SL-D10, each with options and a working recommendation. |

## Brief coverage

| Brief item | Analysis | Implementation |
| --- | --- | --- |
| 1. Format, kinds, budget, rules/wiki/plugins | [Format](index.html#format) | SL-1 |
| 2. Library scopes, overrides, pins, lifecycle, wiki | [Library](index.html#library) | SL-1, SL-2, SL-8 to SL-10 |
| 3. Triggers, forecast, classifier, conflicts, caching, three numerical cards | [Selection](index.html#selection), [Costs](index.html#economics), [calculation inputs](cost-model.json) | SL-3, SL-6 |
| 4. Full prompt text, resources, CLI adapters, review/fix/concept | [Distribution](index.html#distribution) | SL-4, SL-5 |
| 5. Card view, reverse library view, ledger and outcomes | [Visibility](index.html#visibility) | SL-6, SL-7 |
| 6. Benefit/harm comparisons, independent review, owner feedback | [Quality](index.html#quality) | SL-11 |
| 7. Cross-project sharing, export/import, releases, customer control | [Shipping](index.html#shipping) | SL-2, SL-12, SL-13 |
| 8. Recommended decisions, enforced sequence, first three skill contents | [Open decisions](index.html#open-decisions), [Implementation](index.html#implementation), [Initial skills](index.html#first-skills) | All thirteen slices |

## AGT-3465 continuity

Sources: [AGT-3465 Dossier](../style-rule-delivery/index.html) and its
[workbench source](../style-rule-delivery/workbench.json), dated 3 November 2026.
The latter identifies `sourceTaskKeys: ["AGT-3465"]`, status `decision-pending`,
and nine implementation entries. Those are repository observations. Human
Review is the state reported by this task brief; no fresh live-card state is
claimed. The prior API read failed. The live card may contain later steering.

Recommendation: merge the overlapping work into one implementation programme
under this Dossier, retaining both source histories and stable rule ids. Do not
close, promote or change either card in this concept delivery. SL-1 reconciles
the mapping below with the live card before duplicate work is retired.

| Existing slice | Takeover or change | Successor slices |
| --- | --- | --- |
| SR-S1, format/catalogue | Preserve portable SKILL.md, applicability and stable rule ids. Generalize rule packs to versioned skill packages with explicit instruction closure. | SL-1, SL-2 |
| SR-S2, header-zone pilot | Preserve concrete header rules and checks as project policy content. Reconcile its external AGT-3464 dependency; do not invent pilot approval. Adopt the approved Access/one-look revision through a new package version. | SL-1, SL-10 |
| SR-S3, selector/budget | Preserve deterministic path routing and omission reasons. Replace the 6,000-character ceiling, two optional blocks and pointer tiers with complete closures, token budgets and a bounded ambiguity classifier. | SL-3 |
| SR-S4, Docs/remote/native roots | Preserve card Docs and exact remote payloads. Full prompt transport becomes authoritative; native CLI roots become supplementary. Resource transfer uses the explicit run-context contract. | SL-4, SL-7 |
| SR-S5, rule-fit review | Preserve exact delivered rule ids, citations and selection-miss findings. Broaden review to pinned skill criteria without promoting advisory style to hard policy. | SL-5, SL-11 |
| SR-S6, guide migration | Preserve existing guide aliases and hard-rule identities. Import existing skills and migrate content through reviewed packages; keep legacy readers during rollout. | SL-1, SL-2, SL-8 to SL-10, SL-13 |
| SR-S7, ledger/revision proposals | Preserve criterion-linked findings and ordinary proposal cards. Add invocation/version provenance, owner feedback and harm comparisons before releasing a revision. | SL-6, SL-11 |
| SR-S8, measurement | Preserve precision, misses and delivered-token measurements. Add unique cards, invocations, rework and acceptance denominators; distinguish association from causal comparison. | SL-6, SL-7, SL-11 |
| SR-S9, workspace packs | Preserve cross-project defaults and explicit project settings. Add immutable product/workspace/project identities, adoption locks, forks and portable exports. Mandatory scoped policy remains binding. | SL-2, SL-12, SL-13 |

## Implementation source

The authoritative source is the complete `implementationTasks` array in
[workbench.json](workbench.json). The entries below mirror its order, titles,
`dependsOn` arrays and acceptance criteria. Every entry contains full
`promptMarkdown`, including scope, delivery, acceptance and verification.
The HTML Implementation section now embeds every prompt verbatim in an
expandable slice, and the verifier checks parity against the JSON source.
No prompt is a placeholder or a request to implement the whole programme.
The sequence is SL-1, then SL-2 through SL-13, each depending on its predecessor.
This intentionally serializes changes to enrichment, transport, ledger,
frontend, skill content and release governance. External forecast and UI-pilot
inputs have stated fallbacks; a genuinely blocking external dependency must be
registered through the application API before its implementation card runs.

## SL-1: Skill and receipt contracts

Source: `implementationTasks[0]`, id `SL-1` in [workbench.json](workbench.json).
Scheduler dependencies: none. [Article sequence entry](index.html#SL-1).

Acceptance:

- Schemas validate minimal and full packages and reject conflicting ids, unsafe paths, dependency cycles and missing instructional closure files.
- A reviewed migration table covers all five legacy skills, guide blocks, AGT-3465 criteria and the old concept, with any unavailable source explicitly identified.
- Documented authority and retention rules preserve core permissions, mandatory policy, historical receipts and direct-session lookup.

## SL-2: Immutable registry and pins

Source: `implementationTasks[1]`, id `SL-2` in [workbench.json](workbench.json).
Scheduler dependencies: SL-1. [Article sequence entry](index.html#SL-2).

Acceptance:

- Same id/version with different bytes is rejected; exact retained pins replay after product upgrade or local fork.
- Two projects share one workspace package while one extends it, with distinct provenance and no cross-project knowledge leak.
- Archive and path-containment cases, invalid manifests, conflicting replacements and missing dependencies have bounded typed errors.

## SL-3: Typed selection and budgets

Source: `implementationTasks[2]`, id `SL-3` in [workbench.json](workbench.json).
Scheduler dependencies: SL-2. [Article sequence entry](index.html#SL-3).

Acceptance:

- A backend task containing card, token and design prose does not spuriously select frontend implementation guidance.
- Three relevant small skills can fit; required overflow or mandatory conflict produces an explicit preparation failure; no truncation or silent required omission.
- Identical inputs/locks give identical selection and order; missing forecast and invalid classifier produce conservative declared outcomes.
- Legacy enrichment report readers remain compatible and selector input/output usage is charged once.

## SL-4: Prompt and resource transport

Source: `implementationTasks[3]`, id `SL-4` in [workbench.json](workbench.json).
Scheduler dependencies: SL-3. [Article sequence entry](index.html#SL-4).

Acceptance:

- Fake transports verify byte-identical full closures for all supported adapters, including multiline text, resource bindings and platform newline behavior.
- Remote corruption, path traversal, missing file, excessive archive size or receipt persistence failure prevents launch with a named reason.
- Prompt-equivalence tests preserve task priority, core guardrails and permission settings; opt-in live probes for available CLIs report unsupported ones explicitly.
- Diff inventory proves generated context cannot enter the task result commit.

## SL-5: Review and continuation context

Source: `implementationTasks[4]`, id `SL-5` in [workbench.json](workbench.json).
Scheduler dependencies: SL-4. [Article sequence entry](index.html#SL-5).

Acceptance:

- Review cannot retroactively fail a task against a skill version released after implementation.
- Fix, resumed and newly spawned attempts preserve or explicitly supersede pins without double-counting a resume as a new delivery.
- Concept and role cases have correct required sets and no new execution permissions.
- Criterion-specific findings distinguish requirements, advice and not-applicable scope.

## SL-6: Skill ledger and projections

Source: `implementationTasks[5]`, id `SL-6` in [workbench.json](workbench.json).
Scheduler dependencies: SL-5. [Article sequence entry](index.html#SL-6).

Acceptance:

- Replayed events and transport retries do not inflate delivery counts; prepared-only and failed events do not count as loads.
- Fixtures demonstrate correct unique-card reach, terminal acceptance denominator, pending/censored counts, applicable review findings and rework per card.
- Scoped library sums reconcile to child projections and do not reveal another project beyond caller access.
- Null cache/cost data remains unknown; imported legacy summaries never become full delivery evidence.

## SL-7: Card and library visibility

Source: `implementationTasks[6]`, id `SL-7` in [workbench.json](workbench.json).
Scheduler dependencies: SL-6. [Article sequence entry](index.html#SL-7).

Acceptance:

- Relevant Playwright cases cover both card-to-skill and skill-to-card navigation, filters, permission scope, omitted-budget and unknown-data states.
- Light and dark desktop/phone screenshots prove the touched surfaces; keyboard focus and panel scrolling work.
- Visible totals match filtered children, including pagination semantics; no colored left accent bars or local status primitives.
- No claim of native delivery is inferred from a catalogue listing or sentinel echo.

## SL-8: Frontend best-practice artefact

Source: `implementationTasks[7]`, id `SL-8` in [workbench.json](workbench.json).
Scheduler dependencies: SL-7. [Article sequence entry](index.html#SL-8).

Acceptance:

- Backend prose mentioning UI, card or token does not route to this skill; actual Angular component work does.
- Closure contains all requirements and remains within the release budget without summary truncation.
- Behavioral evidence covers row identity/focus, stale async response and changed user journey, with no content loss.
- The package has an independent release identity and retrievable source provenance.

## SL-9: Backend best-practice artefact

Source: `implementationTasks[8]`, id `SL-9` in [workbench.json](workbench.json).
Scheduler dependencies: SL-8. [Article sequence entry](index.html#SL-9).

Acceptance:

- Declared stack/path triggers and exclusions pass representative positive and negative cases.
- Duplicate-request, authorization and cancellation scenarios are behaviorally assessed, and boundary guard tests remain green where touched.
- Source mappings, complete closure count, licence, owner and immutable release evidence are present.
- Skill instructions do not prescribe a second scheduler or direct workspace mutation.

## SL-10: UX and visual-standard artefact

Source: `implementationTasks[9]`, id `SL-10` in [workbench.json](workbench.json).
Scheduler dependencies: SL-9. [Article sequence entry](index.html#SL-10).

Acceptance:

- A content-heavy concept is not forced into implementation or decorative-layout instructions and retains every required acceptance item.
- UI behavior, relevant states, keyboard use, content coverage and both themes are included in evaluation evidence.
- Approved versus candidate visual standards are visibly distinct in package provenance and selection.
- No unseen owner comparison data is presented as a replicated measurement.

## SL-11: Skill quality loop

Source: `implementationTasks[10]`, id `SL-11` in [workbench.json](workbench.json).
Scheduler dependencies: SL-10. [Article sequence entry](index.html#SL-11).

Acceptance:

- A deliberately harmful candidate that improves visual compliance but drops required content fails promotion.
- No mandatory policy is withheld; arm leakage, missing outputs and evaluator disagreements remain visible.
- Every reported outcome has a cohort, denominator, version, evaluator and uncertainty; usage alone cannot trigger release.
- Feedback creates a reviewed draft/revision path and cannot edit a published package in place.

## SL-12: Portable releases and plugins

Source: `implementationTasks[11]`, id `SL-12` in [workbench.json](workbench.json).
Scheduler dependencies: SL-11. [Article sequence entry](index.html#SL-12).

Acceptance:

- Round-trip export/import into an isolated installation preserves instruction and resource hashes offline.
- Malformed, unsigned-unadopted, incompatible, oversized and path-traversing packages do not activate or execute.
- Customer overrides survive product upgrade and produce distinct receipt lineage.
- No credentials, private card content, usage history or enabled connector grants leak through default export.

## SL-13: Compatibility rollout and documentation

Source: `implementationTasks[12]`, id `SL-13` in [workbench.json](workbench.json).
Scheduler dependencies: SL-12. [Article sequence entry](index.html#SL-13).

Acceptance:

- Every supported matrix cell has a full transport receipt and correct pinned skills, or a named unsupported state without an unenriched success claim.
- Controlled pilot shows no missing mandatory guidance, unexplained omissions or billing double count.
- Rollback restores a prior lock for new runs and leaves in-flight and historical receipts intact.
- Documentation clearly states shipped capability, remaining limits, AGT-3465 disposition and how customers create and share skills.

## Collection

The required review content lives in the repository entrypoint and descriptor.
Linked source pages are provenance, not a prerequisite for inspecting the four
required sections or the complete implementation-card sequence. The managed
snapshot currently copies those two entry files first. The verifier reports an
entrypoint-only inventory so that this distinction is explicit.

Recreate the collected review packet after a repair or results cleanup:

```sh
python3 -B docs/portable-skill-library/prepare-review.py --results-dir "$JOB_RESULTS_DIR"
```

The helper validates the canonical Dossier before writing. It writes only to
the explicitly supplied absolute task results folder. A follow-up card uses
its own `JOB_RESULTS_DIR`; the helper does not write outside that directory.
It does not change task lanes or project state.
It produces:

- `review-entry.md`: a compact requirement and sequence table, linking the
  actual section texts in `sections/` and full card prompts in `cards/`.
- `report.html`: the entire canonical HTML entrypoint, byte-identical.
- `report.md`: the entire readable article, including all thirteen full prompts.
- `required-sections.md`: the actual Alternatives, Recommendation, Evidence and
  Open decisions section text rather than a claim that those sections exist.
- `implementation-cards.md`: every full `promptMarkdown` plus explicit scheduler
  ids and `dependsOn` arrays in delivery order.
- `dossier/`: byte-identical copies of all files in this Dossier directory.
- `workbench.json`, `review-map.md`, `evidence.md`, `cost-model.json`: directly
  inspectable descriptor and supporting documents beside the reports.
- `deliverables.md` and `status.md` inside the results folder: the exact
  canonical repository entrypoint and packet inventory.
- `verification.json` and `review-manifest.json`: structural, collected-byte and
  prompt-parity checks, file sizes and SHA-256 identity.

At the start of AGT-4036, the canonical Dossier was absent from the assigned
checkout and no review packet had been collected for this follow-up card.
`initial-delivery-verification-before.json` retains that initial observation. It is not a reconstruction
of an earlier review input. New
document captures, when present, validate this HTML only. They are not
screenshots of implemented skill-library product surfaces.

The collection command is repeatable. Rerun it after any Dossier edit or new
capture so that the copied bytes and file manifest describe the final packet.
Local verification cannot certify which bytes a later remote review consumes;
its file hashes provide a concrete reference for that handoff.

### Integration recovery

AGT-4036's `merge-into-develop` recovery retains the integrated descriptor key
`AGT-W3470` and the complete AGT-4032 content while keeping the collection and
verification repairs described above. The task's `integration-recovery.md`
links the failed stage, source SHAs, five file resolutions and verification
evidence. `integration-recovery.json` records content comparisons against both
the supplied base and the original delivery. The runner owns publication and
integration; this document does not claim that a gate has already run.

## Verification

Run from the repository root:

```sh
python3 docs/portable-skill-library/verify.py
python3 -B docs/portable-skill-library/verify.py --results-dir "$JOB_RESULTS_DIR"
```

The verifier parses the actual HTML, validates required metadata, substantive
section presence, ten recommended decisions, all thirteen complete prompts and
ordered dependency arrays, local link targets, article-template CSS identity,
entry-file snapshot budgets and all three cost scenarios. It checks that this
map retains every slice's acceptance criteria. It emits JSON with file hashes
so a reviewer can identify the verified bytes. A pass establishes these checks;
content judgment, real CLI transport and skill effectiveness remain separate.

The canonical entrypoint is `docs/portable-skill-library/index.html`. Task
references are collected in `deliverables.md`, `review-entry.md` and `status.md`
inside `JOB_RESULTS_DIR`. Those files are delivery evidence for the same Dossier.
The task results also contain the verification output and document captures.
