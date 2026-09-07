/**
 * The structured model behind the Result view.
 *
 * A {@link ResultDocument} is a single, presentation-ready projection of a
 * finished run that the Result view renders in three layers, top to bottom:
 *
 *   1. a **summary head** (code-review grade, duration, tokens, commits)
 *   2. an **overview** ("problem -> solution", the shareable one-liner)
 *   3. the **detail** markdown (What Was Done / Open Items / Notes / Images)
 *
 * It is built purely on the client from two sources that are already in scope
 * in the protocol pane - `status.md` markdown and the task metadata - so the
 * redesign needs no backend round-trip and works for every historical run.
 * When the backend later writes a structured `result.json` this same shape is
 * the target; `buildResultDocument` is then just one of two producers.
 *
 * Keeping the parse here (pure, unit-tested) rather than in the component keeps
 * the view thin and lets the case + metric logic be hammered branch by branch.
 */
import type { TaskDetail, ReviewProjectionView } from '../../../../models/task.model';
import { formatTokens } from '../../../../services/format.util';
import { buildTokenCostTooltip } from '../../../tokens';
import type { ProtocolVerdict } from './protocol-verdict';
import { classifyResultCase, type ResultCaseResult } from './result-case';

/** One stat in the summary head. Only stats with real data are emitted. */
export interface ResultMetric {
  id: string;
  label: string;
  value: string;
  tooltip?: string;
  /** Semantic state retained for tooltips and downstream projections. */
  tone?: 'ok' | 'warn' | 'problem' | 'neutral';
}

/** The shareable overview: problem -> solution, plus whether it was synthesized. */
export interface ResultOverview {
  problem: string | null;
  solution: string | null;
  /** True when there was no explicit `## Overview`; the lines were derived. */
  synthesized: boolean;
}

export interface ResultDocument {
  case: ResultCaseResult;
  overview: ResultOverview;
  metrics: ResultMetric[];
  /** Markdown for the detail layer: `# Status` + `## Overview` stripped out. */
  detailMarkdown: string;
}

const CODE_REVIEW_GRADE_RE = /^code-review:grade-([abcd])$/i;

/**
 * Pull an optional `# Status` header metric line (`- Files: 5`, `- Tests: 12
 * passed`) out of the protocol. Only the header block matters - a later body
 * bullet that happens to start with the same word must not be mistaken for the
 * head metric - so the scan stops at the first `## ` section heading. Returns
 * the trimmed value, or null when the line is absent or empty.
 */
export function parseHeaderMetric(markdown: string | null | undefined, key: string): string | null {
  if (!markdown) return null;
  const lines = markdown.replace(/\r\n/g, '\n').split('\n');
  const re = new RegExp(`^\\s*[-*]?\\s*${key}\\s*:\\s*(.+)$`, 'i');
  for (const line of lines) {
    if (/^##\s+/.test(line)) break; // header block ends at the first H2
    const m = re.exec(line);
    if (m) {
      const value = m[1].trim();
      return value.length > 0 ? value : null;
    }
  }
  return null;
}

/**
 * Turn a raw `- Tests:` value into a compact stat + tone. Recognises an `X/Y`
 * tally (some failed -> warn), a bare "N passed" (all green -> ok), and any
 * value mentioning a failure (-> problem). Anything else renders neutrally with
 * the text as-is so an unexpected phrasing still surfaces the number.
 */
export function classifyTestsMetric(raw: string): { value: string; tone: ResultMetric['tone'] } {
  const text = raw.trim();
  const lower = text.toLowerCase();
  const ratio = /(\d+)\s*\/\s*(\d+)/.exec(text);
  if (ratio) {
    const passed = Number(ratio[1]);
    const total = Number(ratio[2]);
    const tone: ResultMetric['tone'] = passed < total ? 'warn' : 'ok';
    return { value: `${passed}/${total}${passed === total ? ' ✓' : ''}`, tone };
  }
  if (/\bfail|\bbroke|\berror/.test(lower)) return { value: text, tone: 'problem' };
  const passed = /^(\d+)\s+passed$/i.exec(text);
  if (passed) return { value: `${passed[1]} ✓`, tone: 'ok' };
  if (/\bpass|\bgreen|\bok\b/.test(lower)) return { value: text, tone: 'ok' };
  return { value: text, tone: 'neutral' };
}

/** Compact the duration copy supplied by the verdict without changing its value. */
export function compactDurationMetric(value: string): string {
  return value
    .trim()
    .replace(/(\d+(?:\.\d+)?)\s*(?:hours?|hrs?)(?=\b)/gi, '$1h')
    .replace(/(\d+(?:\.\d+)?)\s*(?:minutes?|mins?)(?=\b)/gi, '$1m')
    .replace(/(\d+(?:\.\d+)?)\s*(?:seconds?|secs?)(?=\b)/gi, '$1s')
    .replace(/\s+/g, ' ');
}

const GRADE_META: Record<string, { tone: ResultMetric['tone']; tooltip: string }> = {
  A: { tone: 'ok', tooltip: 'Code review grade A - clean, ship it.' },
  B: { tone: 'ok', tooltip: 'Code review grade B - minor nits only.' },
  C: { tone: 'warn', tooltip: 'Code review grade C - concerns worth a look.' },
  D: { tone: 'problem', tooltip: 'Code review grade D - blocking issues.' },
};

/**
 * Read the code-review letter grade (A-D) out of the task tags. The grade is
 * carried as a `code-review:grade-<a-d>` tag by the review step; there is no
 * first-class field for it on the detail model, so we re-derive it here rather
 * than reaching across into the board feature's badge helper.
 */
export function codeReviewGradeFromTags(tags: readonly string[] | null | undefined): string | null {
  if (!tags) return null;
  for (const tag of tags) {
    const m = CODE_REVIEW_GRADE_RE.exec(tag);
    if (m) return m[1].toUpperCase();
  }
  return null;
}

/**
 * AGT-2717: fallback review stat from the canonical review projection when the
 * task carries no local `code-review:grade-*` tag - the AGT-2689 case, where
 * every round is a Remote Review report and this header used to show nothing
 * for "Review" at all. Reads the same rounds/outcome/blocking-aspect facts as
 * the escalation banner and the board chip, so the three surfaces agree.
 */
export function reviewMetricFromProjection(
  projection: ReviewProjectionView | null | undefined,
): ResultMetric | null {
  if (!projection || projection.rounds === 0) return null;
  const blocking = projection.blockingAspects[0] ?? null;
  const tone: ResultMetric['tone'] = blocking || projection.delivery.status === 'gate-failed'
    ? 'problem'
    : projection.latestOutcome?.toLowerCase() === 'pass'
      ? 'ok'
      : 'neutral';
  const roundsLabel = projection.rounds === 1 ? '1 round' : `${projection.rounds} rounds`;
  const value = projection.latestOutcome ? `${roundsLabel} · ${projection.latestOutcome}` : roundsLabel;
  const tooltip = [
    `${roundsLabel} of review${projection.latestPlane ? ` (latest via ${projection.latestPlane})` : ''}.`,
    blocking ? `Blocked by ${blocking.aspect}: ${blocking.reason}.` : null,
  ].filter(Boolean).join(' ');
  return { id: 'review', label: 'Review', value, tone, tooltip };
}

/** Split markdown into `## Heading` -> body sections (ignores content before the first `##`). */
function splitH2Sections(markdown: string): { heading: string; body: string }[] {
  const lines = markdown.replace(/\r\n/g, '\n').split('\n');
  const out: { heading: string; body: string[] }[] = [];
  let current: { heading: string; body: string[] } | null = null;
  for (const line of lines) {
    const m = /^##\s+(.*)$/.exec(line);
    if (m) {
      if (current) out.push(current);
      current = { heading: m[1].trim(), body: [] };
    } else if (current) {
      current.body.push(line);
    }
  }
  if (current) out.push(current);
  return out.map((s) => ({ heading: s.heading, body: s.body.join('\n').trim() }));
}

function sectionBody(markdown: string, heading: string): string | null {
  const target = heading.toLowerCase();
  const hit = splitH2Sections(markdown).find((s) => s.heading.toLowerCase() === target);
  return hit ? hit.body : null;
}

/** First bullet (or first non-empty line) of a section, stripped of markdown noise. */
function firstBullet(body: string | null): string | null {
  if (!body) return null;
  for (const line of body.split('\n')) {
    const t = line.trim();
    if (!t) continue;
    const text = t.replace(/^[-*]\s+/, '').trim();
    if (!text || text.toLowerCase() === 'none.' || text.toLowerCase() === 'none') continue;
    return text;
  }
  return null;
}

/**
 * Parse a `## Overview` section. Recognises explicit `- Problem:` / `- Solution:`
 * label lines; otherwise treats the first two non-empty lines as problem then
 * solution. Returns null when the section is absent so the caller can synthesize.
 */
function parseOverviewSection(markdown: string): ResultOverview | null {
  const body = sectionBody(markdown, 'Overview');
  if (body === null) return null;
  const labelled = (key: string): string | null => {
    const re = new RegExp(`^\\s*[-*]?\\s*${key}\\s*:\\s*(.+)$`, 'im');
    const m = re.exec(body);
    return m ? m[1].trim() : null;
  };
  let problem = labelled('Problem') ?? labelled('Goal') ?? labelled('Question');
  let solution = labelled('Solution') ?? labelled('Result') ?? labelled('Outcome');
  if (!problem && !solution) {
    const lines = body
      .split('\n')
      .map((l) => l.replace(/^[-*]\s+/, '').trim())
      .filter(Boolean);
    problem = lines[0] ?? null;
    solution = lines[1] ?? null;
  }
  if (!problem && !solution) return null;
  return { problem, solution, synthesized: false };
}

/**
 * Synthesize an overview when status.md has no `## Overview`: the task title is
 * the problem/goal, and the first "What Was Done" bullet is the solution. This
 * keeps the overview-first layout meaningful for every legacy run.
 */
function synthesizeOverview(detail: TaskDetail, markdown: string): ResultOverview {
  const title = detail.info.title?.trim() || null;
  const solution = firstBullet(sectionBody(markdown, 'What Was Done'));
  return { problem: title, solution, synthesized: true };
}

/** Drop the `# Status` block and the `## Overview` block from the detail markdown. */
function stripHeaderAndOverview(markdown: string): string {
  const lines = markdown.replace(/\r\n/g, '\n').split('\n');
  const out: string[] = [];
  let i = 0;
  const isStatusHeading = (l: string) => /^#{1,2}\s+Status\s*$/i.test(l);
  const isOverviewHeading = (l: string) => /^##\s+Overview\s*$/i.test(l);
  const isAnyHeading = (l: string) => /^#{1,6}\s+/.test(l);
  while (i < lines.length) {
    if (lines[i].includes('agent-studio:')) {
      i++;
      continue;
    }
    if (isStatusHeading(lines[i]) || isOverviewHeading(lines[i])) {
      i++;
      while (i < lines.length && !isAnyHeading(lines[i])) i++;
      continue;
    }
    out.push(lines[i]);
    i++;
  }
  let start = 0;
  while (start < out.length && out[start].trim() === '') start++;
  return out.slice(start).join('\n').trimEnd();
}

function buildMetrics(detail: TaskDetail, verdict: ProtocolVerdict, markdown: string): ResultMetric[] {
  const metrics: ResultMetric[] = [];
  const info = detail.info;

  const grade = codeReviewGradeFromTags(info.tags);
  if (grade) {
    const meta = GRADE_META[grade] ?? { tone: 'neutral' as const, tooltip: `Code review grade ${grade}.` };
    metrics.push({ id: 'grade', label: 'Review', value: `Grade ${grade}`, tone: meta.tone, tooltip: meta.tooltip });
  } else {
    const reviewMetric = reviewMetricFromProjection(info.reviewProjection);
    if (reviewMetric) metrics.push(reviewMetric);
  }

  if (verdict.duration) {
    metrics.push({ id: 'duration', label: 'Duration', value: compactDurationMetric(verdict.duration) });
  }

  // Files changed + tests passed: the two quality-head metrics the Result
  // redesign deferred in Teil 1. They ride optional `# Status` header lines the
  // summarizer emits only when the run log proves a real number, so a stat
  // appears exactly when there is honest data behind it.
  const filesRaw = parseHeaderMetric(markdown, 'Files');
  if (filesRaw) {
    const n = Number(filesRaw.match(/\d+/)?.[0]);
    const value = Number.isFinite(n) ? `${n} file${n === 1 ? '' : 's'}` : filesRaw;
    metrics.push({
      id: 'files',
      label: 'Files',
      value,
      tooltip: 'Files changed by this task (from the run diff).',
    });
  }

  const testsRaw = parseHeaderMetric(markdown, 'Tests');
  if (testsRaw) {
    const { value, tone } = classifyTestsMetric(testsRaw);
    metrics.push({
      id: 'tests',
      label: 'Tests',
      value,
      tone,
      tooltip: `Test outcome reported by the run: ${testsRaw}.`,
    });
  }

  const totalTokens = info.tokenSummary?.totalTokens ?? 0;
  if (totalTokens > 0) {
    const tokenSummary = info.tokenSummary!;
    metrics.push({
      id: 'tokens',
      label: 'Tokens',
      value: `${formatTokens(totalTokens)} tokens`,
      tooltip: buildTokenCostTooltip({
        costUsd: tokenSummary.estimatedApiCostUsd,
        priceKnown: tokenSummary.allModelsPriced === true,
        context: `${totalTokens.toLocaleString()} tokens across this task's runs.`,
      }),
    });
  }

  const commitCount = info.commits?.length ?? 0;
  if (commitCount > 0) {
    metrics.push({
      id: 'commits',
      label: 'Commits',
      value: `${commitCount} commit${commitCount === 1 ? '' : 's'}`,
    });
  } else if (info.codeActivityDetected === true) {
    metrics.push({ id: 'commits', label: 'Commits', value: 'pending', tooltip: 'Work landed but the attributed commit chain is still resolving.' });
  } else if (info.codeActivityDetected === false) {
    metrics.push({ id: 'commits', label: 'Commits', value: 'no code change', tooltip: 'The run moved no code (analysis / docs / investigation).' });
  }

  return metrics;
}

/**
 * Build the {@link ResultDocument} for a finished run from its detail payload
 * and the already-derived head verdict. `verdict` is passed in so the single
 * Result case badge is authoritative; it is deliberately not repeated as a
 * metric beside itself.
 */
export function buildResultDocument(detail: TaskDetail, verdict: ProtocolVerdict): ResultDocument {
  const markdown = detail.statusMarkdown ?? '';
  const scaffold = markdown.includes('agent-studio:result-scaffold');
  const overview = scaffold
    ? { problem: null, solution: null, synthesized: true }
    : parseOverviewSection(markdown) ?? synthesizeOverview(detail, markdown);
  const detailMarkdown = stripHeaderAndOverview(markdown);
  const caseResult = classifyResultCase({
    hint: parseCaseHint(markdown),
    taskType: detail.info.taskType,
    mode: detail.info.mode,
    verdictKind: verdict.kind,
    verdictLabel: verdict.label,
    body: markdown,
  });

  return {
    case: caseResult,
    overview,
    metrics: buildMetrics(detail, verdict, markdown),
    detailMarkdown,
  };
}

const CASE_HINT_RE = /^\s*-?\s*Case:\s*([A-Za-z][A-Za-z /_-]*)\s*$/im;

/** Pull an optional `- Case: <x>` hint the summary prompt may have emitted. */
export function parseCaseHint(markdown: string | null | undefined): string | null {
  if (!markdown) return null;
  const m = CASE_HINT_RE.exec(markdown);
  return m ? m[1].trim() : null;
}
