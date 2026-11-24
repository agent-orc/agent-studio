# Evidence register

As of 24 November 2026. Source card: AGT-4032. This register separates repository observations, owner-supplied facts, proposed contracts and planning calculations. It does not certify operator acceptance or a product implementation.

Repository source revision rechecked for this repair: `3e292026b2c48307588da208cfc34ce6c36690af`. Product files were inspected read-only and remain unchanged in this task.

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

The current collection check was repeated before writing delivery files and
failed with a missing `results/deliverables.md`; that failure is retained in
`verification-before.json` in the task results. Repository-only validation
already passed for all four required sections, ten recommended decisions and
thirteen full implementation prompts. The observed result gap is therefore
distinct from content absence in this checkout.

[ConceptDossierReviewMaterial.cs](../../backend/Features/Runner/ConceptDossierReviewMaterial.cs)
reads the task reference and queues `index.html` and `workbench.json` first,
with limits of 256,000 characters per file and 512,000 total. The complete
decision and card contract fits in these first two files; the verifier measures
their final sizes and checks every HTML prompt against the descriptor.
[ConceptDossierSnapshot.cs](../../backend/Features/Pipeline/ConceptDossierSnapshot.cs)
copies those two contract files at an exact revision, so completeness must not
depend on following a supporting source link. This is code inspection, not an
observation of the previous remote review invocation.

[prepare-review.py](prepare-review.py) makes the delivery packet reproducible
after results cleanup. It collects the complete entrypoint in HTML and readable
text, all four actual section extracts, the thirteen complete prompts and
dependency arrays, all Dossier files, both requested task references, verification
and a hash manifest. The post-collection check compares those files with the
canonical bytes. It proves local packet completeness and identity; later remote
ingestion, concept acceptance and operator approval remain pipeline work.

The initial collected results directory in this repair contained only
`preparation-manifest.json`; `results/deliverables.md` and the prior verification
artifacts named by `status.md` were absent. This is an observed handoff gap,
not proof of the cause of either earlier review finding. The repair retains
the canonical repository Dossier, embeds all thirteen full implementation
prompts in the HTML entrypoint, and verifies them against the JSON source.
A byte-identical Dossier copy, a readable full-prompt inventory, structural
verification and a file/hash manifest are collected under the task's absolute
results directory. Both task-file references name the canonical repository
entrypoint. No prior absent verification artifact is claimed as new evidence.

The local AGT-3465 Dossier and its nine slices remain mapped in
[review-map.md](review-map.md#agt-3465-continuity). Its live card remains
unretrieved; the API failure below belongs to the prior inspection.
The current source checks reconfirmed the enrichment limits, selection,
summary rendering and report seam. Official OpenAI pricing/caching and Claude
caching pages were reopened; the stated reference-route rates remain the
planning inputs. This is not a live cache measurement.

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

All were fetched or opened again during this repair on 24 November 2026. Links are reference contracts, not pinned production price records.

- [Official OpenAI skills documentation](https://developers.openai.com/codex/skills): portable SKILL.md, resources, native discovery and the distinction between skills and plugin packaging. Studio chooses its own managed distribution; the provider's preferred marketplace/plugin route is not a requirement here.
- [OpenAI API pricing](https://developers.openai.com/api/docs/pricing): Standard reference rates used in the planning model. Tier and context length matter; a subscription is not API billing.
- [OpenAI prompt caching](https://developers.openai.com/api/docs/guides/prompt-caching): matching prefix, model-specific write/read behavior and usage reporting. A CLI cannot be assumed to expose every API control.
- [Claude prompt caching](https://platform.claude.com/docs/en/build-with-claude/prompt-caching): model-specific pricing, matching prefix and cache lifetime.
- [Claude Code headless mode](https://code.claude.com/docs/en/headless): programmatic invocation and prompt transport. This concept does not recommend changing the repository's permission flags or enabling bare mode.

## Calculation provenance

[cost-model.json](cost-model.json) contains all scenario inputs and calculated outputs. Each scenario uses the same planning token amount for both providers; this is deliberately an estimate, not claimed tokenizer equivalence. Ten-request reuse assumes one write and nine full reads within a valid cache lifetime. Failed reuse, partial-prefix reuse, extra model calls and output costs can increase real spend.

No controlled skill-quality experiment, tokenization experiment, native CLI compatibility test or live cache measurement was performed. The implementation prompts explicitly require these where material. Proposed budgets, archive caps, routing classifier limits, evaluation sample target and harm thresholds are recommendations.

## Delivery constraints

The explicit concept-mode instruction limits repository changes to this single Dossier directory. The normal docs-index row is therefore deferred to SL-1 rather than editing `docs/start/README.md` in this run. The canonical HTML reuses the current article-document-v2 style block, font families, color variables and evidence components. No existing product-state claim is supported by a fabricated or seeded screenshot. Document-layout captures in task results validate this Dossier only.
