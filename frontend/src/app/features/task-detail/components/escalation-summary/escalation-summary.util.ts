/**
 * Pure view-model builder for the escalation summary panel (AGT-2019).
 *
 * A `5e-escalated` card only showed the thin status protocol of the LAST run,
 * never WHY it escalated or what was already delivered (operator feedback
 * Robert 2026-07-09). This module aggregates the artifacts that already exist
 * for such a card into one decision-ready view, with no new persistence:
 *
 *   1. ESCALATION REASON - concrete open findings from the latest structured
 *      council reaction, then structured steering findings or review evidence.
 *      Follow-up Markdown remains an artifact and never feeds banner copy.
 *   2. REVIEW VERDICT - the code-review grade + verdict + summary parsed from
 *      the newest `code-review-grade-*.md` frontmatter (already served by
 *      `GET …/code-review/list`).
 *   3. DELIVERY CONTEXT - is the work already in develop / main, and how many
 *      commits / files it carries (from `TaskInfo.mergeSignal` + `commits`).
 *   4. RECOMMENDATION - the gate's steer (accept as-is / reissue / needs
 *      decision), derived from `orchestratorVerdict` when present.
 *
 * Kept dependency-light and pure so the aggregation rules are unit-tested in
 * isolation from the Angular host, mirroring `pipeline-groups.util` /
 * `steering-detail.model`.
 */
import type { TaskInfo } from '../../../../models/task.model';
import type { ReviewEvidenceEntry, ReviewEvidenceSeverity } from '../../../../models/task.model';
import type {
  CodeReviewListEntry,
  CouncilFindingAssessment,
} from '../../../../services/task.service';
import {
  codeReviewVerdictTone,
  type CodeReviewVerdictTone,
} from '../code-review-verdict.util';
import {
  aspectVerdictTone,
  type AspectFinding,
  type AspectVerdictTone,
} from '../../../../components/aspect-findings';
import type { SteeringInfo } from '../../../../components/steering-detail';
import { buildMergeSignal, type MergeSignalView } from '../../../board';
import type { TaskTimelineEvent } from '../../../task-timeline';

/** One open gate point, rendered as a checklist row. */
export interface EscalationGateItem {
  /** Stable identity for keyed rendering. */
  id: string;
  /** Human text of the gate point. */
  text: string;
  /** Structured explanation kept separate from the finding title. */
  detail?: string | null;
  /** Checklist state; `false` (unchecked) means still open. */
  checked: boolean;
  /** Optional per-aspect verdict token (e.g. `block`) for a tone chip. */
  verdict?: string | null;
  /** Central tone for the optional verdict chip. */
  tone?: AspectVerdictTone;
}

/** Where {@link EscalationSummaryView.gateItems} was sourced from. */
export type EscalationGateSource = 'council-reaction' | 'gate-evidence' | 'review-evidence' | 'none';

/** Compact review-verdict head, from the newest code-review grade file. */
export interface EscalationReviewHead {
  /** Quality grade `A`/`B`/`C`/`D`, or null for older verdict-only reviews. */
  grade: string | null;
  /** Tone for the grade chip. */
  gradeTone: AspectVerdictTone;
  /** Raw verdict token (`pass`/`concerns`/`block`). */
  verdict: string;
  /** Tone for the verdict chip. */
  verdictTone: CodeReviewVerdictTone;
  /** One-line reviewer summary. */
  summary: string;
  /** Model that produced the grade, for provenance. */
  model: string | null;
  /** ISO instant the grade ran, for provenance. */
  runAt: string | null;
  /** True when a newer review artifact exists but has no fresh grade. */
  olderDelivery: boolean;
}

/** One automatic reissue and the timeline reason that caused it. */
export interface EscalationReissue {
  index: number;
  at: string;
  trigger: string;
}

/** Delivery context: where the work landed + how much of it there is. */
export interface EscalationDelivery {
  /** Two-segment develop/main merge signal, or null when nothing is committed. */
  merge: MergeSignalView | null;
  /** Number of commits attributed to the task. */
  commitCount: number;
  /** Distinct changed files across those commits (0 when unknown). */
  filesChanged: number;
  /** Repository delivery lines from the canonical integration projection. */
  repositories: string[];
}

/** The single structured line that remains visible when details are closed. */
export interface EscalationEssence {
  reviewRounds: number;
  latestGrade: string | null;
  openFindings: number;
  reasonClass: string;
  label: string;
}

/**
 * DtC step 6 — the escalation-category families that mean the ORCHESTRATOR (or
 * the infra under it) could not conclude and handed the task to a human: the
 * "GaveUpToHuman" terminal. These read distinctly from a logical NeedsReview,
 * where the agent itself concluded the work needs a human's judgement
 * (`[[TASK_BLOCKED]]` / `[[TASK_NEEDS_INPUT]]`) or a quality gate flagged it.
 *
 * The set mirrors the backend `HumanReviewEscalationCategories` give-up members
 * (infra crash / inconclusive / quota / environmental / cli-launch / watchdog /
 * pickup-zombie / empty-fast-exit / context-overflow / model-invalid /
 * quarantined / auto-failure-park). Source = the escalation category the runtime
 * already writes into the `status.md` stub and the orchestrator log — no new
 * side-channel.
 */
const GAVE_UP_CATEGORIES = new Set<string>([
  'infra-crash',
  'orchestrator-inconclusive',
  'inconclusive-with-results',
  'quota-exhausted',
  'environmental',
  'cli-launch-failed',
  'watchdog-kill',
  'pickup-zombie',
  'empty-fast-exit',
  'context-overflow',
  'model-invalid',
  'quarantined',
  'auto-failure-park',
]);

/** Presentable labels for the escalation categories the give-up banner shows. */
const CATEGORY_LABELS: Record<string, string> = {
  'infra-crash': 'Infra crash',
  'orchestrator-inconclusive': 'Orchestrator inconclusive',
  'inconclusive-with-results': 'Inconclusive (partial results)',
  'quota-exhausted': 'Quota exhausted',
  environmental: 'Environmental fault',
  'cli-launch-failed': 'CLI launch failed',
  'watchdog-kill': 'Watchdog kill',
  'pickup-zombie': 'Pickup zombie',
  'empty-fast-exit': 'Empty fast exit',
  'context-overflow': 'Context overflow',
  'model-invalid': 'Model invalid',
  quarantined: 'Quarantined',
  'auto-failure-park': 'Auto-failure park',
};

/** Turn a raw category slug into a human label (title-cases unknown slugs). */
export function escalationCategoryLabel(category: string): string {
  const key = category.trim().toLowerCase();
  if (CATEGORY_LABELS[key]) return CATEGORY_LABELS[key];
  return key
    .split(/[-_\s]+/)
    .filter(Boolean)
    .map((w) => w.charAt(0).toUpperCase() + w.slice(1))
    .join(' ');
}

/**
 * `HumanReviewEscalation.BuildStatusStub` writes `- Category: <slug>` and
 * `- Reason: <text>` into the escalated card's `status.md` when a system
 * escalation left no agent-written summary (the exact infra-crash / inconclusive
 * give-up case). Lift those two lines back out so the panel can name the
 * category. Returns null when the status has no `Category:` line — i.e. the card
 * carries a real agent summary (a logical / quality escalation), which is
 * itself the signal that it is NOT a system give-up.
 */
export function parseStatusStubEscalation(
  statusMarkdown: string | null | undefined,
): { category: string; reason: string | null } | null {
  if (!statusMarkdown) return null;
  let category: string | null = null;
  let reason: string | null = null;
  for (const line of statusMarkdown.split(/\r?\n/)) {
    const cat = /^\s*-\s*Category:\s*(.+\S)\s*$/i.exec(line);
    if (cat && !category) category = cat[1].trim();
    const rea = /^\s*-\s*Reason:\s*(.+\S)\s*$/i.exec(line);
    if (rea && !reason) reason = rea[1].trim();
  }
  return category ? { category: category.toLowerCase(), reason } : null;
}

/**
 * Classification of an escalated card into the DtC terminal it reached:
 * `gave-up` (the orchestrator/infra could not conclude — GaveUpToHuman) vs
 * `needs-review` (a logical / quality escalation a human judges on its merits).
 * Only `gave-up` gets the distinct prominent banner; `needs-review` keeps the
 * standard escalation presentation.
 */
export interface EscalationClassView {
  kind: 'gave-up' | 'needs-review';
  /** Raw category slug (e.g. `infra-crash`), when one was recovered. */
  category: string | null;
  /** Human label for the category chip, when known. */
  categoryLabel: string | null;
  /** One-line honest reason, from the status stub or the steering event. */
  reason: string | null;
}

/**
 * Derive the escalation class from the escalation category the runtime already
 * recorded. Priority: the `status.md` stub category (reliable for the primary
 * system-escalation path, including infra-crash), then the steering event's
 * `cause` / reason text (the aspect-verdict infra path writes
 * `aspect-verdict-infra-crash`). A card with an escalate verdict but no
 * recognisable give-up category is a logical NeedsReview. Returns null when the
 * card is not an escalation at all.
 */
export function deriveEscalationClass(
  inputs: Pick<EscalationSummaryInputs, 'info' | 'statusMarkdown' | 'steering'>,
): EscalationClassView | null {
  const isEscalation = inputs.info.orchestratorVerdict === 'escalate';
  const stub = parseStatusStubEscalation(inputs.statusMarkdown);
  const cause = causeOf(inputs.steering);
  const steeringReason = inputs.steering?.reason?.trim() || null;

  // Category, best available: the stub slug wins; else sniff the steering cause
  // (e.g. `aspect-verdict-infra-crash`) for a known give-up token.
  let category = stub?.category ?? null;
  if (!category && cause) {
    const hit = [...GAVE_UP_CATEGORIES].find((c) => cause.toLowerCase().includes(c));
    if (hit) category = hit;
  }

  const isGiveUp = !!category && GAVE_UP_CATEGORIES.has(category);
  if (!isGiveUp && !isEscalation) return null;

  return {
    kind: isGiveUp ? 'gave-up' : 'needs-review',
    category,
    categoryLabel: category ? escalationCategoryLabel(category) : null,
    reason: stub?.reason ?? steeringReason,
  };
}

/** The three gate recommendations the operator chooses between. */
export type EscalationRecommendationKind = 'accept' | 'reissue' | 'needs-decision';

/** Recommendation line derived from the orchestrator verdict. */
export interface EscalationRecommendation {
  kind: EscalationRecommendationKind;
  /** Operator-facing label (e.g. "Needs decision"). */
  label: string;
  /** Tone driving the pill colour. */
  tone: 'ok' | 'warn' | 'danger';
}

/** The full aggregated view model the panel renders. */
export interface EscalationSummaryView {
  /**
   * DtC step 6 — whether the orchestrator/infra gave up (GaveUpToHuman) or a
   * human must review a logical/quality escalation. Drives the distinct
   * give-up banner. Null when the card is not an escalation.
   */
  escalation: EscalationClassView | null;
  /** One-line escalation reason headline (from the steering event). */
  reason: string | null;
  /** Machine cause label (e.g. `completion-gate`), when recorded. */
  cause: string | null;
  /** The open gate points, as a checklist. */
  gateItems: EscalationGateItem[];
  /** Which artifact the gate items came from. */
  gateSource: EscalationGateSource;
  /** Compact review-verdict head, or null when no grade/verdict exists. */
  review: EscalationReviewHead | null;
  /** Delivery context (merge status + commit / file counts). */
  delivery: EscalationDelivery;
  /** Gate recommendation, or null when no verdict is recorded. */
  recommendation: EscalationRecommendation | null;
  /** Reissue history derived from quality-loop reopen rows. */
  reissues: EscalationReissue[];
  /** Structured, bounded banner copy. Never derived from Markdown bodies. */
  essence: EscalationEssence;
}

/** Inputs the host feeds in from the existing polled / fetched signals. */
export interface EscalationSummaryInputs {
  info: TaskInfo;
  reviewEvidence: readonly ReviewEvidenceEntry[];
  codeReviews: readonly CodeReviewListEntry[];
  /** Latest escalate/reissue steering info from the timeline, or null. */
  steering: SteeringInfo | null;
  /**
   * The card's `status.md` body (`TaskDetail.statusMarkdown`). Carries the
   * escalation category + reason for a system escalation (via the
   * `BuildStatusStub` `- Category:` / `- Reason:` lines); null when absent.
   */
  statusMarkdown: string | null;
  /** Full chronological task ledger used for reissue provenance and budget. */
  timeline: readonly TaskTimelineEvent[];
}

/** Project the latest council sidecar's typed finding decisions. */
export function gateItemsFromCouncil(
  assessments: readonly CouncilFindingAssessment[],
): EscalationGateItem[] {
  return assessments.map((assessment, index) => ({
    id: `council-${index}-${assessment.finding}`,
    text: assessment.finding,
    detail: assessment.reason,
    checked: assessment.action === 'Accept',
    verdict: councilActionLabel(assessment.action),
    tone: assessment.action === 'Accept'
      ? 'ok'
      : assessment.action === 'FixNextRound'
        ? 'warn'
        : 'danger',
  }));
}

/**
 * Turn the structured aspect findings carried on an escalate/reissue steering
 * event into gate items. Each finding is an open point: label + verdict chip +
 * reason, rendered unchecked (they are the reasons the gate did NOT pass).
 */
export function gateItemsFromFindings(findings: readonly AspectFinding[]): EscalationGateItem[] {
  return findings.map((f, index) => {
    const reason = f.reason?.trim();
    const text = reason ? `${f.aspect}: ${reason}` : f.aspect;
    return {
      id: `gate-${index}-${f.aspect}`,
      text,
      checked: false,
      verdict: f.verdict || null,
      tone: aspectVerdictTone(f.verdict),
    };
  });
}

/** Severity rank for evidence ordering (high first). */
const EVIDENCE_RANK: Record<ReviewEvidenceSeverity, number> = { high: 0, warn: 1, info: 2 };

/** Map an evidence severity to a verdict tone for its chip. */
function evidenceTone(severity: ReviewEvidenceSeverity): AspectVerdictTone {
  switch (severity) {
    case 'high':
      return 'danger';
    case 'warn':
      return 'warn';
    default:
      return 'neutral';
  }
}

/**
 * Last-resort gate items from the task-level review evidence: the unacknowledged
 * findings, high severity first. Acknowledged findings are dropped — the
 * operator has already dispositioned them, so they are no longer "open".
 */
export function gateItemsFromEvidence(entries: readonly ReviewEvidenceEntry[]): EscalationGateItem[] {
  return [...entries]
    .filter((e) => !e.acknowledged)
    .sort((a, b) => (EVIDENCE_RANK[a.severity] ?? 3) - (EVIDENCE_RANK[b.severity] ?? 3))
    .map((e) => ({
      id: `evidence-${e.id}`,
      text: e.title,
      checked: false,
      verdict: e.severity,
      tone: evidenceTone(e.severity),
    }));
}

/** Choose findings from typed sources only, newest council reaction first. */
export function resolveGateItems(
  inputs: Pick<EscalationSummaryInputs, 'codeReviews' | 'steering' | 'reviewEvidence'>,
): { items: EscalationGateItem[]; source: EscalationGateSource } {
  const newest = newestCodeReview(inputs.codeReviews);
  const council = gateItemsFromCouncil(newest?.councilReaction?.assessments ?? []);
  if (council.length > 0) return { items: council, source: 'council-reaction' };

  const findings = gateItemsFromFindings(inputs.steering?.openItems ?? []);
  if (findings.length > 0) return { items: findings, source: 'gate-evidence' };

  const evidence = gateItemsFromEvidence(inputs.reviewEvidence);
  if (evidence.length > 0) return { items: evidence, source: 'review-evidence' };

  return { items: [], source: 'none' };
}

function councilActionLabel(action: CouncilFindingAssessment['action']): string {
  if (action === 'FixNextRound') return 'fix next round';
  if (action === 'Escalate') return 'escalate';
  return 'accepted';
}

function newestCodeReview(
  entries: readonly CodeReviewListEntry[],
): CodeReviewListEntry | null {
  return [...entries].sort((a, b) => (b.runAt ?? '').localeCompare(a.runAt ?? ''))[0] ?? null;
}

/** Map a code-review grade letter to a tone chip. A → ok … D → danger. */
export function gradeTone(grade: string | null | undefined): AspectVerdictTone {
  switch ((grade ?? '').trim().toUpperCase()) {
    case 'A':
      return 'ok';
    case 'B':
      return 'ok';
    case 'C':
      return 'warn';
    case 'D':
      return 'danger';
    default:
      return 'neutral';
  }
}

/**
 * Pick the review-verdict head from the code-review list. Prefers the newest
 * entry that carries a quality grade (the `code-review-grade-*.md` pass); falls
 * back to the newest entry with any verdict so a card reviewed only by the
 * older verdict path still shows a head. Null when the list is empty.
 *
 * The list endpoint already returns entries newest-first, but we do not rely on
 * that: we compare `runAt` explicitly so an unsorted caller is still correct.
 */
export function pickReviewHead(entries: readonly CodeReviewListEntry[]): EscalationReviewHead | null {
  if (entries.length === 0) return null;
  const byNewest = [...entries].sort((a, b) => (b.runAt ?? '').localeCompare(a.runAt ?? ''));
  const graded = byNewest.find((e) => !!e.grade?.trim());
  const chosen = graded ?? byNewest[0];
  if (!chosen) return null;
  const grade = chosen.grade?.trim() || null;
  return {
    grade,
    gradeTone: gradeTone(grade),
    verdict: chosen.verdict || 'unknown',
    verdictTone: codeReviewVerdictTone(chosen.verdict),
    summary: chosen.summary?.trim() || '',
    model: chosen.model?.trim() || null,
    runAt: chosen.runAt || null,
    olderDelivery: chosen !== byNewest[0],
  };
}

/**
 * Lift every automatic reopen into a compact history row. Timeline details are
 * preferred because they carry the concrete gate cause; the human summary is
 * the fallback for older ledgers.
 */
export function deriveReissues(events: readonly TaskTimelineEvent[]): EscalationReissue[] {
  return events
    .filter((event) => event.kind === 'quality_loop_reopened')
    .map((event, index) => {
      const cause = event.details?.['cause']?.trim();
      const reason = event.details?.['reason']?.trim();
      const summary = event.summary?.trim();
      const trigger = cause && reason && !reason.toLowerCase().includes(cause.toLowerCase())
        ? `${cause}: ${reason}`
        : reason || cause || summary || 'Quality loop reopened the task.';
      return { index: index + 1, at: event.ts, trigger };
    });
}

/** Compose the bounded banner line from typed review, finding and timeline data. */
export function buildEscalationEssence(inputs: {
  codeReviews: readonly CodeReviewListEntry[];
  gateItems: readonly EscalationGateItem[];
  timeline: readonly TaskTimelineEvent[];
  steering: SteeringInfo | null;
}): EscalationEssence {
  const reviewRounds = inputs.codeReviews.filter(
    (entry) => !!entry.grade?.trim() || /^code-review-grade-/i.test(entry.fileName),
  ).length;
  const latestGrade = pickReviewHead(inputs.codeReviews)?.grade ?? null;
  const openFindings = inputs.gateItems.filter((item) => !item.checked).length;
  const reasonClass = escalationReasonClass(inputs.timeline, inputs.steering, inputs.codeReviews);
  const roundsLabel = reviewRounds === 1 ? '1 review round' : `${reviewRounds} review rounds`;
  const findingsLabel = openFindings === 1 ? '1 open finding' : `${openFindings} open findings`;
  return {
    reviewRounds,
    latestGrade,
    openFindings,
    reasonClass,
    label: `${roundsLabel} · Grade ${latestGrade ?? 'not recorded'} · ${findingsLabel} · ${reasonClass}`,
  };
}

/** Classify the escalation without inspecting reason or follow-up prose. */
export function escalationReasonClass(
  events: readonly TaskTimelineEvent[],
  steering: SteeringInfo | null,
  codeReviews: readonly CodeReviewListEntry[],
): string {
  const escalation = [...events].reverse().find((event) => event.kind === 'orchestrator_escalated');
  const attempt = Number(escalation?.details?.['attempt']);
  const maxAttempts = Number(escalation?.details?.['maxAttempts']);
  if (Number.isFinite(attempt) && Number.isFinite(maxAttempts) && maxAttempts > 0 && attempt >= maxAttempts) {
    return 'Reissue budget exhausted';
  }

  const cause = causeOf(steering)?.toLowerCase() ?? '';
  const gaveUpCategory = [...GAVE_UP_CATEGORIES].find((category) => cause.includes(category));
  if (gaveUpCategory) return escalationCategoryLabel(gaveUpCategory);
  if (cause === 'completion-gate') return 'Completion gate';
  if (cause === 'needs-input-escalate') return 'Input required';
  if (cause.includes('aspect-verdict')) return 'Aspect gate';
  if (newestCodeReview(codeReviews)?.councilReaction?.disposition === 'Escalate') {
    return 'Council review escalated';
  }
  return 'Human decision required';
}

/**
 * Build the delivery context: the develop/main merge signal plus commit and
 * distinct-file counts. Files are counted as the union of every commit's
 * `files` list so a file touched by two commits is not double-counted; when no
 * commit carries a file list we fall back to summing `filesChanged`.
 */
export function buildDelivery(info: TaskInfo): EscalationDelivery {
  const merge = buildMergeSignal(info);
  const commits = info.commits ?? (info.commit ? [info.commit] : []);
  const distinctFiles = new Set<string>();
  let filesChangedSum = 0;
  for (const c of commits) {
    filesChangedSum += c.filesChanged ?? 0;
    for (const f of c.files ?? []) distinctFiles.add(f);
  }
  const filesChanged = distinctFiles.size > 0 ? distinctFiles.size : filesChangedSum;
  const repositories = (info.integration?.repositories ?? []).map((repository) => {
    const integrated = repository.commits.filter((commit) => commit.onIntegrationBranch).length;
    const branch = repository.integrationBranch || 'develop';
    const release = repository.releaseBranch || 'main';
    const targets = repository.onReleaseBranch && release !== branch
      ? `${branch} and ${release}`
      : branch;
    const reason = repository.onIntegrationBranch ? '' : ` (${repository.detail})`;
    return `${repository.repository} ${integrated}/${repository.commits.length} ${targets}${reason}`;
  });
  return { merge, commitCount: commits.length, filesChanged, repositories };
}

/**
 * Derive the gate recommendation line from the orchestrator verdict. Escalated
 * cards carry `escalate` (the gate handed the call to a human → "Needs
 * decision"); a card that reached escalation after a reissue/accept verdict
 * maps to the corresponding steer. Null when no verdict is recorded.
 */
export function deriveRecommendation(
  verdict: TaskInfo['orchestratorVerdict'],
): EscalationRecommendation | null {
  switch (verdict) {
    case 'accept':
      return { kind: 'accept', label: 'Accept as-is', tone: 'ok' };
    case 'reissue':
      return { kind: 'reissue', label: 'Reissue', tone: 'warn' };
    case 'escalate':
      return { kind: 'needs-decision', label: 'Needs decision', tone: 'danger' };
    default:
      return null;
  }
}

/** Assemble the full escalation summary view model from the raw inputs. */
export function buildEscalationSummaryView(inputs: EscalationSummaryInputs): EscalationSummaryView {
  const { items, source } = resolveGateItems(inputs);
  const delivery = buildDelivery(inputs.info);
  const reason = inputs.steering?.reason?.trim() || null;
  return {
    escalation: deriveEscalationClass(inputs),
    reason,
    cause: causeOf(inputs.steering),
    gateItems: items,
    gateSource: source,
    review: pickReviewHead(inputs.codeReviews),
    delivery,
    recommendation: deriveRecommendation(inputs.info.orchestratorVerdict),
    reissues: deriveReissues(inputs.timeline),
    essence: buildEscalationEssence({
      codeReviews: inputs.codeReviews,
      gateItems: items,
      timeline: inputs.timeline,
      steering: inputs.steering,
    }),
  };
}

/** Pull the `Cause` context row out of the steering info, when present. */
function causeOf(steering: SteeringInfo | null): string | null {
  if (!steering) return null;
  const row = steering.context.find((c) => c.key.toLowerCase() === 'cause');
  return row?.value?.trim() || null;
}
