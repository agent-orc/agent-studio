# Evidence register

As of 24 November 2026. Source card: AGT-4032. This register separates repository observations, owner-supplied facts, proposed contracts and planning calculations. It does not certify operator acceptance or a product implementation.

Original AGT-4032 source inspection: `3e292026b2c48307588da208cfc34ce6c36690af`. AGT-4036 restores the Dossier from retained commit `fb5bfbeb99de31ccbcc736b522eb09dfa25b47f6` onto base `e3ca3c8adc8024dcb76dd9b34798723de14de212`. Product files remain unchanged by this repair. The observations and external-reference checks below belong to AGT-4032 unless explicitly labelled AGT-4036.

## Repository observations

| Source | Inspected symbol or section | Established fact | Limit |
| --- | --- | --- | --- |
| [IntakeRunner.cs](../../backend/Features/Runner/IntakeRunner.cs) | `MaxEnrichmentContextCharacters`, `MaxEnrichmentEstimatedTokens`, `MaxOptionalEnrichmentBlocks`, `BuildEnrichmentManifest`, `DetectTaskAreas`, `ApplyEnrichmentBudget` | 6,000-character / approximately 1,500-token budget, two optional blocks; case-insensitive substring detection; guide priorities header 10, frontend 20, other 30; omissions recorded. | Source inspection, not a fresh replay of production cards. Same-area guides tie; the owner's equal-priority description is broader than this checkout. |
| [ProjectStyleGuideService.cs](../../backend/Features/Docs/ProjectStyleGuideService.cs) | `MaxPromptSummaryChars`, catalogue validation | `promptSummary` has a 600-character ceiling. | The renderer appends the summary and source attribution, not the full guide. |
| [PromptEnrichmentService.cs](../../backend/Features/Runner/PromptEnrichmentService.cs) | `Prepare`, `RenderLaunchContext`, `BuildReport`, `WriteAtomic` | Shared preparation produces exact context plus report; context precedes report persistence; failed report persistence blocks dispatch; report includes hashes, exact block text, candidate decisions, token estimates and costs. Selector exceptions currently fail open to the authored prompt with a warning. | Delivery is not proof that the model used the instructions. Mandatory-skill fail-closed behavior is a proposed change. |
| [PromptEnrichmentReport.cs](../../backend/Shared/Models/PromptEnrichmentReport.cs) | `PromptEnrichmentReport`, `PromptEnrichmentBlock`, `PromptEnrichmentTokens` | A reusable audit model exists; attributed appended tokens are already part of the core prompt and must not be billed twice. | It is not yet the proposed per-invocation immutable skill ledger. |
| [Pipeline domain](../system/domains/pipeline.md) | Enrichment service ownership and PRE-step description | The existing step id, project convention and local/remote seam are established. | This concept preserves those seams and changes payload/selection semantics. |
| [Skill library](../../.agents/skills/README.md) | Active skills and CLI discovery notes | Five repository skills, portable source folders, documented Codex discovery and Claude visual-design link. | CLI versions in that document were checked by another task. No fresh native-discovery experiment was performed here. |
| [Portable architecture](../concepts/skills-architecture.md) | Decision, prompt stack, storage and risks | Earlier concept already favors portable shared knowledge and CLI adapters; proposes excerpts and confirmation before automatic suggestions. | This proposal explicitly changes those choices. Preserve legacy lookup consumers until migration. |
| [Dispatcher Dossier](../dispatcher-personas/index.html) | Touch forecast, DS-D4 and role contract | Shared cited-path/module forecast with producer confidence is the intended reusable input. | A concept/release history is not proof that every runtime caller supplies this input. |
| [Persona team](../persona-team/index.html) | PT-D1 and role/activation model | Versioned persona descriptors and workspace activations are distinct from prompt-only skills. | A skill can belong to a role but does not obtain its grants. |
| [Hard rules](../quality/design/style-guide-hard-rules.md) | R1 to R9 | Existing binding Studio UI constraints, including theme support, semantic status, flat admin surfaces and keyboard containment. | Customer applications adopt their own scoped policy; Studio rules do not automatically apply to every project. |
| [Guide index](../quality/README.md) | Applicability contract | Bounded catalogue, typed project/technology applicability, source warnings and snapshots exist. | These can be reused without keeping the 600-character summary as the skill instruction body. |
| [Claude one-shot](../../backend/Features/Cli/Routing/OneShot/ClaudeOneShot.cs) and [Codex one-shot](../../backend/Features/Cli/Routing/OneShot/CodexOneShot.cs) | Standard input and process arguments | Both repository one-shot paths send prompt text through stdin; Codex builds an `exec` invocation with final `-`. | Does not prove every main-run adapter, resume mode or installed CLI version accepts the proposed new payload; SL-4 and SL-13 must test them. |

## Review repair

Repair card: AGT-4036.

Finding: required Dossier content and the complete card sequence could not be
verified. At the start of AGT-4036, the assigned checkout had neither
`docs/portable-skill-library/index.html` nor its descriptor. The task results
contained only the preparation manifest. `verification-before.json` in this
repair's results records that observation. It does not establish what a prior
remote reviewer received.

AGT-4032's retained commit `fb5bfbeb99de31ccbcc736b522eb09dfa25b47f6`
and collected artifacts contain seven complete Dossier files. AGT-4036 restores
those files to the assigned checkout and adds the documentation-index entry.
The recovered source passes the section, dependency, prompt-parity, navigation
and cost-arithmetic checks before the collection helper is updated.

The repaired [collection helper](prepare-review.py) accepts the explicitly
supplied absolute task results directory, including a follow-up card's folder.
It writes all evidence inside that directory. It does not write a sibling
job-folder status file. `review-entry.md` links the actual text of all four
required sections and thirteen separate complete card prompts. Their dependency
arrays and titles remain inspectable without opening a large combined report.
The complete HTML, readable report, descriptor, supporting sources and a
byte-identical Dossier snapshot are also collected. Verification checks the
actual content and HTML/descriptor prompt parity; the manifest records file
sizes and SHA-256 identity. Collection is repeatable after results cleanup.

[ConceptDossierReviewMaterial.cs](../../backend/Features/Runner/ConceptDossierReviewMaterial.cs)
queues the entrypoint and descriptor first with 256,000 characters per file
and 512,000 total. Both complete contract files fit those limits.
[ConceptDossierSnapshot.cs](../../backend/Features/Pipeline/ConceptDossierSnapshot.cs)
copies these two files at the exact source revision. The required section text
and all full card prompts are therefore embedded in the entrypoint itself,
without relying on a supporting source link. These are source-code observations;
AGT-4036 does not change the review pipeline or claim to observe a later review.

The concept's recommended options remain the working decisions for promoted
implementation cards unless a recorded operator decision selects another.
This repair preserves the concept and sequence; it does not implement SL-1
through SL-13 or claim decision acceptance. Local verification establishes
collected content identity and completeness, with document rendering checked
separately. Sight review and operator approval remain pipeline lanes.


## Integration recovery

The failed `merge-into-develop` stage reported conflicts in this register,
`prepare-review.py`, `review-map.md`, `verify.py` and `workbench.json` against
`09ec17dd03ccd6a426ce12a079e1611a7b8439a0`. The continuation checkout starts
at `7278095b721bfae3e69715671e02a7c043524fbb` with no unresolved Git entries.
AGT-4036 reapplies the follow-up packet collection behavior from retained delivery
`0ef1c0e980ce50deebe4727d1463048a7a403aea`: task-specific results confinement,
individual required-section extracts, all thirteen individual full prompts and
verification of those files. The develop-assigned Dossier key `AGT-W3470` is
preserved. No merge, commit rewrite or integration-branch mutation is performed.

In this continuation the canonical Dossier already passes repository checks,
but the assigned results folder again contains only preparation metadata.
`verification-before.json` records the missing collected reference before
packaging. The earlier build/test gate notice supplies no failed test names or
command output. The Task API read is unavailable because this runner has no
operator-token file; no credentials or task pins are changed. Current build,
focused tests, guards and lint are recorded separately in the collected run
report, without claiming to reconstruct that gate's failure.

## Owner input

The authored task is the source for these facts and directions:

- On 24 November, the owner requires task-fitting knowledge delivered in full through the prompt, independent of native CLI skill support, with versions, cross-project sharing and usage visible from both cards and skills.
- AGT-3465 concerns style/design rule packs, applicability, card Docs delivery and review against delivered rules; the brief says it is in Human Review and undecided.
- A blind design comparison on 24 November favored the variant without the visual-design skill because of content depth. No original artefacts, scoring sheet, sample size, model controls or assignment method were supplied. The Dossier treats it as a reported observation and testable harm hypothesis.
- The Access-page pilot and the parallel “one look and feel” concept will produce future UI standards. This task does not establish their final approved content, keys or delivery dates.

Read attempt: `node .agents/skills/task-api/scripts/find-tasks.js --grep '3465'` returned `ECONNREFUSED 127.0.0.1:5031`. No service was started and no task state was changed. The original AGT-3465 acceptance criteria therefore remain unretrieved. The local repository Dossier and all nine of its implementation entries are available and mapped during the repair. SL-1 includes reconciliation of that mapping, the live card and its external AGT-3464 pilot dependency through the API before duplicate work is retired. The task brief provides a defensible merger recommendation without blocking this concept.

## Inventory

Measured entrypoint text sizes in this checkout, using Python `len(Path.read_text())` and the existing intake approximation `(characters + 3) // 4`:

| SKILL.md | Characters | Estimated tokens |
| --- | ---: | ---: |
| operator | 15,068 | 3,767 |
| task-api | 16,599 | 4,150 |
| visual-design | 11,720 | 2,930 |
| runtime-log-analysis | 12,746 | 3,187 |
| regenerate-readme | 5,200 | 1,300 |

These are entrypoint-only character estimates. References, scripts, wrappers and other resources are not included. Actual provider tokenizers differ. This inventory supports a migration procedure class rather than pretending every existing skill fits a new small cap.

## External primary references

All were fetched or opened by AGT-4032 on 24 November 2026; AGT-4036 preserves these dated references without a new pricing or provider-contract claim. Links are reference contracts, not pinned production price records.

- [Official OpenAI skills documentation](https://developers.openai.com/codex/skills): portable SKILL.md, resources, native discovery and the distinction between skills and plugin packaging. Studio chooses its own managed distribution; the provider's preferred marketplace/plugin route is not a requirement here.
- [OpenAI API pricing](https://developers.openai.com/api/docs/pricing): Standard reference rates used in the planning model. Tier and context length matter; a subscription is not API billing.
- [OpenAI prompt caching](https://developers.openai.com/api/docs/guides/prompt-caching): matching prefix, model-specific write/read behavior and usage reporting. A CLI cannot be assumed to expose every API control.
- [Claude prompt caching](https://platform.claude.com/docs/en/build-with-claude/prompt-caching): model-specific pricing, matching prefix and cache lifetime.
- [Claude Code headless mode](https://code.claude.com/docs/en/headless): programmatic invocation and prompt transport. This concept does not recommend changing the repository's permission flags or enabling bare mode.

## Calculation provenance

[cost-model.json](cost-model.json) contains all scenario inputs and calculated outputs. Each scenario uses the same planning token amount for both providers; this is deliberately an estimate, not claimed tokenizer equivalence. Ten-request reuse assumes one write and nine full reads within a valid cache lifetime. Failed reuse, partial-prefix reuse, extra model calls and output costs can increase real spend.

No controlled skill-quality experiment, tokenization experiment, native CLI compatibility test or live cache measurement was performed. The implementation prompts explicitly require these where material. Proposed budgets, archive caps, routing classifier limits, evaluation sample target and harm thresholds are recommendations.

## Delivery constraints

AGT-4032 limited its changes to this Dossier directory. AGT-4036 also adds the required navigation row in `docs/start/README.md`. The canonical HTML reuses the current article-document-v2 style block, font families, color variables and evidence components. No existing product-state claim is supported by a fabricated or seeded screenshot. Document-layout captures in task results validate this Dossier only.
