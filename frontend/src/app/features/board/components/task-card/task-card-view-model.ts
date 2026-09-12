import { TaskState } from '../../../../models/task.model';
import type { TaskInfo, ClientSummary, CliType, TagRegistryEntry, EpicRollup, AutoLoopSnapshot, PendingIntent, TaskMode } from '../../../../models/task.model';
import type { TaskCommitInfo } from '../../../../features/git';
import type { StructuredTooltip } from 'coding-agent-chat/shared';
import type { MenuItem } from '../../../../components/menu';
import type { AutoReviewStatusView } from '../../../../services/auto-review-status.store';
import { cliTypeIcon, cliTypeLabel, shortModelName, taskModeIcon, taskModeLabel } from '../../../../services/format.util';
import { shouldShowFailureToast } from '../../../task-detail/services/run-outcome.util';
import { buildThinkingLevelIndicator, type ThinkingLevelIndicator } from '../../../../services/thinking-level.util';
import { phaseStaticLabel } from '../../../../services/lifecycle-phase.util';
import { isTaskRunActive } from '../../../../services/run-activity.util';
import { buildTokenCostTooltip, formatTokenCostDisplay } from '../../../tokens';
import { lanePresentation } from '../../../../models/lane-presentation';

export interface TaskTypeChip {
  kind: string;
  label: string;
  icon: string;
  tooltip: string;
}

export interface TaskTokenBubbleEntry {
  ts: string;
  tsLabel: string;
  model: string | null;
  total: number;
  /** Cost estimate priced at this entry's own timestamp, not today's rate. */
  costLabel: string;
}

export interface TaskTokenBubble {
  label: string;
  total: number;
  input: number;
  output: number;
  cacheRead: number;
  cacheWrite: number;
  /** Short single-line honest total, e.g. "$1.25" or "incomplete (1 run without price)". */
  costLine: string;
  /** Full estimate caveat + pricing-gap detail; rendered in a hover tooltip, not inline. */
  costTooltip: string;
  model: string | null;
  lastUpdate: string | null;
  tier: 'neutral' | 'blue' | 'mauve' | 'peach';
  entries: TaskTokenBubbleEntry[];
}

const FILE_LIST_MAX = 12;

export function buildTaskTypeChip(taskType: TaskInfo['taskType']): TaskTypeChip {
  const type = (taskType || 'chore').toLowerCase();
  if (type === 'bug') return { kind: 'bug', label: 'Bug', icon: 'warn', tooltip: 'Task type: Bug' };
  if (type === 'feature' || type === 'user-story') {
    return { kind: 'feature', label: 'Feature', icon: 'plus', tooltip: 'Task type: Feature' };
  }
  return { kind: 'chore', label: 'Chore', icon: 'dot', tooltip: 'Task type: Chore (default)' };
}

export interface ModeBadge {
  mode: TaskMode;
  label: string;
  icon: string;
  tooltip: string;
}

const MODE_TOOLTIP: Record<Exclude<TaskMode, 'coding'>, string> = {
  planning: 'Planning mode: read-only. The agent investigates and produces a plan without writing source.',
  research: 'Research mode: read-only with web access. The agent gathers information and reports findings.',
  concept: 'Concept mode: one docs-only Dossier awaiting human review.',
};

/**
 * Mode badge for the card. Only non-coding modes get a badge so that the board
 * stays quiet for the common case (coding is the default) while planning and
 * research cards are immediately recognizable. Glyphs come from `format.util`
 * so they match the create-dialog mode picker. Returns null for coding or when
 * the field is absent (older payloads).
 */
export function buildModeBadge(mode: TaskInfo['mode']): ModeBadge | null {
  if (mode !== 'planning' && mode !== 'research' && mode !== 'concept') return null;
  return {
    mode,
    label: taskModeLabel(mode),
    icon: taskModeIcon(mode),
    tooltip: MODE_TOOLTIP[mode],
  };
}

export function buildCommitTooltip(commit: TaskInfo['commit']): StructuredTooltip | string {
  if (!commit) return '';
  const subject = (commit.message || '').split('\n')[0];
  const files = commit.files ?? [];
  const title = `${commit.shortSha} - ${commit.filesChanged} file(s) changed`;
  const parts: string[] = [];
  if (subject) {
    parts.push(`<div>${escapeHtml(subject)}</div>`);
  }
  if (files.length > 0) {
    const shown = files.slice(0, FILE_LIST_MAX);
    const overflow = files.length - shown.length;
    const items = shown
      .map((file) => `<li><code>${escapeHtml(file)}</code></li>`)
      .join('');
    parts.push(`<ul>${items}</ul>`);
    if (overflow > 0) {
      parts.push(`<div><small>+${overflow} more file(s)</small></div>`);
    }
  }
  if (parts.length === 0) {
    return { title, body: `${commit.filesChanged} file(s) changed` };
  }
  return { title, body: parts.join('') };
}

/** Newest-first commit chain for a task. SSOT: prefer `commits[]`; fall back
 *  to the legacy singular `commit` only when `commits[]` is absent/empty, so
 *  older payloads still render. Never sources from repo HEAD. */
export function commitChainOf(job: TaskInfo): TaskCommitInfo[] {
  const chain = job.commits && job.commits.length > 0
    ? job.commits
    : (job.commit ? [job.commit] : []);
  // Stored oldest -> newest; the card shows the latest commit first.
  return [...chain].reverse();
}

export interface CommitRowView {
  shortSha: string;
  subject: string;
  filesChanged: number;
  hasFiles: boolean;
  tooltip: StructuredTooltip | string;
}

export interface CommitChainView {
  variant: 'full' | 'review';
  rows: CommitRowView[];
  moreCount: number;
  totalCount: number;
  anyFiles: boolean;
  moreTooltip: StructuredTooltip | string;
}

/** How many commit rows the card renders before collapsing to "+N more"
 *  (AC#4: 2-3 commits show in full; >3 show top-3 plus a disclosure). */
const COMMIT_ROWS_MAX = 3;

function commitSubject(commit: TaskCommitInfo): string {
  return (commit.message || '').split('\n')[0].trim();
}

export function buildCommitChainView(job: TaskInfo, variant: 'full' | 'review'): CommitChainView | null {
  const chain = commitChainOf(job);
  if (chain.length === 0) return null;
  const shown = chain.slice(0, COMMIT_ROWS_MAX);
  const rows: CommitRowView[] = shown.map((c) => ({
    shortSha: c.shortSha,
    subject: commitSubject(c),
    filesChanged: c.filesChanged,
    hasFiles: (c.files?.length ?? 0) > 0,
    tooltip: buildCommitTooltip(c),
  }));
  const rest = chain.slice(COMMIT_ROWS_MAX);
  return {
    variant,
    rows,
    moreCount: rest.length,
    totalCount: chain.length,
    anyFiles: rows.some((r) => r.hasFiles),
    moreTooltip: rest.length > 0 ? buildMoreCommitsTooltip(rest) : '',
  };
}

function buildMoreCommitsTooltip(rest: TaskCommitInfo[]): StructuredTooltip {
  const items = rest
    .map((c) => `<li><code>${escapeHtml(c.shortSha)}</code> ${escapeHtml(commitSubject(c))} <small>(${c.filesChanged} file(s))</small></li>`)
    .join('');
  return { title: `${rest.length} more commit(s)`, body: `<ul>${items}</ul>` };
}

// Lanes where the per-task commit chain is shown. Review lanes render the
// `review` variant; 3-progress shows the `full` variant that prefixes each row
// with the ⏺ glyph so the working agent can correlate it with its own
// auto-commit. Every other lane hides the chain entirely.
const COMMIT_PILL_LANES = new Set<string>([TaskState.Progress, TaskState.AutoReview, TaskState.HumanReview, TaskState.Escalated, '4-review']);
const COMMIT_REVIEW_LANES = new Set<string>([TaskState.AutoReview, TaskState.HumanReview, TaskState.Escalated, '4-review']);

/** Which commit-chain variant a lane renders, or null when the lane shows no
 *  chain. Keeps the lane->variant policy in one testable place instead of as
 *  component statics. */
export function commitChainVariant(state: string): 'full' | 'review' | null {
  if (!COMMIT_PILL_LANES.has(state)) return null;
  return COMMIT_REVIEW_LANES.has(state) ? 'review' : 'full';
}

/** Chain-aware commit tooltip. A single commit reuses the per-commit file
 *  list; a multi-commit chain lists every SHA with its subject and the rolled
 *  up file-change total. Empty chain -> no tooltip. Never sources repo HEAD. */
export function buildCommitChainTooltip(job: TaskInfo): StructuredTooltip | string {
  const chain = commitChainOf(job);
  if (chain.length === 0) return '';
  if (chain.length === 1) return buildCommitTooltip(chain[0]);
  const totalFiles = chain.reduce((sum, c) => sum + (c.filesChanged ?? 0), 0);
  const items = chain
    .map((c) => `<li><code>${escapeHtml(c.shortSha)}</code> ${escapeHtml(commitSubject(c))} <small>(${c.filesChanged} file(s))</small></li>`)
    .join('');
  return {
    title: `${chain.length} commits - ${totalFiles} file(s) changed`,
    body: `<ul>${items}</ul>`,
  };
}

export interface CommitEmptyBadge {
  tone: 'no-code' | 'discovery';
  label: string;
  tooltip: string;
}

/** Zero-commit diagnostic for review-lane cards (AC#3, bug (3)). Only fires in
 *  review lanes and only when the attributed chain is genuinely empty.
 *  `codeActivityDetected` (scanner signal, never repo HEAD) and the canonical
 *  delivery ref disambiguate the two cases the operator could not tell apart:
 *   - `false` -> analysis-only task: a calm "no code changes" badge.
 *   - `true` or a delivery ref -> work exists but no commit is attributed: an amber
 *     "commit discovery pending" diagnostic so a lost/undiscovered commit is
 *     visibly different from a correct no-op. */
export function buildCommitEmptyBadge(job: TaskInfo): CommitEmptyBadge | null {
  if (!COMMIT_REVIEW_LANES.has(job.state)) return null;
  if (commitChainOf(job).length > 0) return null;
  const deliveryRef = job.integration?.deliveryRef?.trim() || null;
  if (job.codeActivityDetected || deliveryRef) {
    return {
      tone: 'discovery',
      label: 'commit discovery pending',
      tooltip: deliveryRef
        ? `Delivery ref ${deliveryRef} exists, but no commit is attributed to this task yet. Open the task and check the Git view: the attribution backfill may still be pending, or the rule could not associate the delivered commit. This is NOT an analysis-only task.`
        : 'This task moved repository HEAD during a run, but no commit is attributed to it yet. Open the task and check the Git view: the attribution backfill may still be pending, or a commit landed that the rule could not associate. This is NOT an analysis-only task.',
    };
  }
  return {
    tone: 'no-code',
    label: 'no code changes',
    tooltip: 'No commit is attributed to this task and no run moved repository HEAD. This is an analysis-only / no-op task by design, not a lost commit.',
  };
}

export function buildTokenBubble(tokenSummary: TaskInfo['tokenSummary']): TaskTokenBubble | null {
  if (!tokenSummary) return null;
  const input = tokenSummary.inputTokens ?? 0;
  const output = tokenSummary.outputTokens ?? 0;
  const cacheRead = tokenSummary.cacheReadTokens ?? 0;
  const cacheWrite = tokenSummary.cacheCreationTokens ?? 0;
  const total = input + output + cacheRead + cacheWrite;
  if (total <= 0) return null;
  const tier = total >= 5_000_000 ? 'peach'
    : total >= 500_000 ? 'mauve'
      : total >= 50_000 ? 'blue'
        : 'neutral';
  const entries = (tokenSummary.entries ?? []).map((entry) => {
    const entryTotal = (entry.inputTokens ?? 0) + (entry.outputTokens ?? 0) + (entry.cacheReadTokens ?? 0) + (entry.cacheCreationTokens ?? 0);
    return {
      ts: entry.ts,
      tsLabel: formatShortTime(entry.ts),
      model: entry.displayModel ?? entry.model,
      total: entryTotal,
      // Each run is priced with the rate valid on its own timestamp
      // (entry.estimatedApiCostUsd), never today's rate.
      costLabel: formatTokenCostDisplay({
        costUsd: entry.estimatedApiCostUsd,
        totalTokens: entryTotal,
        unpricedRuns: entry.modelPriced ? 0 : 1,
      }),
    };
  });
  const unpricedRuns = (tokenSummary.entries ?? [])
    .filter((entry) => !entry.modelPriced
      && (entry.inputTokens ?? 0) + (entry.outputTokens ?? 0) + (entry.cacheReadTokens ?? 0) + (entry.cacheCreationTokens ?? 0) > 0)
    .length;
  return {
    label: formatTokens(total),
    total,
    input,
    output,
    cacheRead,
    cacheWrite,
    costLine: formatTokenCostDisplay({ costUsd: tokenSummary.estimatedApiCostUsd, totalTokens: total, unpricedRuns }),
    costTooltip: buildTokenCostTooltip({
      costUsd: tokenSummary.estimatedApiCostUsd,
      priceKnown: tokenSummary.allModelsPriced === true,
      totalTokens: total,
      unpricedRuns,
    }),
    model: tokenSummary.lastModel ?? null,
    lastUpdate: tokenSummary.lastUpdate ? formatShortTime(tokenSummary.lastUpdate) : null,
    tier,
    entries,
  };
}

/** Compact tokens label: 850 -> "850", 2400 -> "2.4k", 850000 -> "850k", 3_100_000 -> "3.1M". */
export function formatTokens(n: number): string {
  if (!isFinite(n) || n <= 0) return '0';
  if (n < 1000) return Math.round(n).toString();
  if (n < 1_000_000) {
    const k = n / 1000;
    return (k >= 100 ? Math.round(k) : Number(k.toFixed(1))) + 'k';
  }
  const m = n / 1_000_000;
  return (m >= 100 ? Math.round(m) : Number(m.toFixed(1))) + 'M';
}

export function formatShortTime(iso: string): string {
  try {
    return new Date(iso).toLocaleString();
  } catch {
    return iso;
  }
}

function escapeHtml(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

export type EffectiveModelSource = 'fallback' | 'run' | 'policy' | 'explicit' | 'default' | 'human' | 'unknown';

export interface EffectiveModelChip {
  icon: string;
  label: string;
  fullModel: string | null;
  cliType: CliType | null;
  cliLabel: string | null;
  source: EffectiveModelSource;
  isDefault: boolean;
  tooltip: StructuredTooltip;
  thinkingLevel: ThinkingLevelIndicator | null;
}

const CLI_TYPES_SET = new Set(['claude', 'codex', 'gemini']);

function isCliType(v: string | null | undefined): v is CliType {
  return !!v && CLI_TYPES_SET.has(v);
}

export function buildEffectiveModelChip(job: TaskInfo, owner: ClientSummary): EffectiveModelChip {
  const execution = job.execution;
  const jobCli = isCliType(job.cliType) ? job.cliType : null;
  const ownerCli = isCliType(owner.defaultCliType) ? owner.defaultCliType : null;
  const ownerModel = owner.defaultModel ?? null;

  let icon: string;
  let label: string;
  let fullModel: string | null;
  let effectiveCliType: CliType | null;
  let cliLbl: string | null;
  let source: EffectiveModelSource;
  let isDefault: boolean;

  if (job.quotaFallback) {
    const cli = isCliType(job.quotaFallback.cliType) ? job.quotaFallback.cliType : null;
    icon = cli ? cliTypeIcon(cli) : '\u{26A0}';
    label = `fallback: ${shortModelName(job.quotaFallback.model)}`;
    fullModel = job.quotaFallback.model;
    effectiveCliType = cli;
    cliLbl = cli ? cliTypeLabel(cli) : null;
    source = 'fallback';
    isDefault = false;
  } else if (execution?.status === 'running' && execution.model) {
    const cli = jobCli ?? ownerCli;
    icon = cli ? cliTypeIcon(cli) : '\u{1F916}';
    label = shortModelName(execution.model);
    fullModel = execution.model;
    effectiveCliType = cli;
    cliLbl = cli ? cliTypeLabel(cli) : null;
    source = 'run';
    isDefault = false;
  } else if (jobCli || job.model) {
    const cli = jobCli ?? ownerCli;
    icon = cli ? cliTypeIcon(cli) : '\u{1F916}';
    label = shortModelName(job.model ?? ownerModel);
    fullModel = job.model ?? ownerModel;
    effectiveCliType = cli;
    cliLbl = cli ? cliTypeLabel(cli) : null;
    source = job.modelExplicit === false ? 'policy' : 'explicit';
    isDefault = false;
  } else if (ownerCli || ownerModel) {
    icon = ownerCli ? cliTypeIcon(ownerCli) : '\u{1F916}';
    label = shortModelName(ownerModel);
    fullModel = ownerModel;
    effectiveCliType = ownerCli;
    cliLbl = ownerCli ? cliTypeLabel(ownerCli) : null;
    source = 'default';
    isDefault = true;
  } else if (owner.kind === 'human') {
    icon = '\u{1F464}';
    label = 'human';
    fullModel = null;
    effectiveCliType = null;
    cliLbl = null;
    source = 'human';
    isDefault = false;
  } else {
    icon = '\u{1F916}';
    label = 'unknown';
    fullModel = null;
    effectiveCliType = null;
    cliLbl = null;
    source = 'unknown';
    isDefault = false;
  }

  const thinkingLevel = buildThinkingLevelIndicator(
    job.execution,
    job.thinkingLevel,
    owner.defaultThinkingLevel,
    fullModel,
  );
  const tooltip = buildModelTooltip(job, owner, source, ownerCli, ownerModel, thinkingLevel);

  return { icon, label, fullModel, cliType: effectiveCliType, cliLabel: cliLbl, source, isDefault, tooltip, thinkingLevel };
}

function buildModelTooltip(
  job: TaskInfo,
  owner: ClientSummary,
  source: EffectiveModelSource,
  ownerCli: CliType | null,
  ownerModel: string | null,
  thinkingLevel: ThinkingLevelIndicator | null,
): StructuredTooltip {
  const lines: string[] = [];

  const jobCli = isCliType(job.cliType) ? job.cliType : null;
  const fallbackCli = isCliType(job.quotaFallback?.cliType) ? job.quotaFallback.cliType : null;
  const effectiveCli = source === 'fallback' ? fallbackCli : jobCli ?? ownerCli;
  const effectiveModel = source === 'fallback'
    ? job.quotaFallback?.model
    : source === 'run'
    ? job.execution?.model ?? job.model ?? ownerModel
    : job.model ?? ownerModel;

  lines.push(`<b>Model:</b> ${escapeHtml(effectiveModel ?? 'none')}${source === 'default' ? ' <i>(client default)</i>' : source === 'run' ? ' <i>(running)</i>' : source === 'policy' ? ' <i>(policy suggestion)</i>' : ''}`);
  lines.push(`<b>CLI:</b> ${effectiveCli ? escapeHtml(cliTypeLabel(effectiveCli)) : 'none'}${!jobCli && ownerCli ? ' <i>(client default)</i>' : ''}`);
  if (thinkingLevel) {
    lines.push(`<b>Thinking level:</b> ${escapeHtml(thinkingLevel.effective)}${thinkingLevel.differsFromConfigured ? ' <i>(effective)</i>' : ''}`);
    if (thinkingLevel.differsFromConfigured) {
      lines.push(`<b>Configured thinking level:</b> ${escapeHtml(thinkingLevel.configured ?? 'none')}`);
    }
  }
  lines.push(`<b>Agent:</b> ${escapeHtml(job.agent || 'none')} <i>(pickup permission)</i>`);
  if (source === 'fallback') lines.push(`<b>Reason:</b> quota (${escapeHtml(job.quotaFallback?.reason ?? 'cap reached')})`);

  const ownerLabel = owner.displayName || owner.id;
  const defaultParts: string[] = [];
  if (ownerCli) defaultParts.push(cliTypeLabel(ownerCli));
  if (ownerModel) defaultParts.push(ownerModel);
  const defaultsStr = defaultParts.length > 0 ? defaultParts.join(' / ') : 'none';
  lines.push(`<b>Owner:</b> ${escapeHtml(ownerLabel)} (${escapeHtml(owner.id)})`);
  lines.push(`<b>Defaults:</b> ${escapeHtml(defaultsStr)}`);

  return {
    title: source === 'fallback' ? 'Quota fallback active' : source === 'run' ? 'Running model' : source === 'policy' ? 'Policy-derived model' : source === 'default' ? 'Effective model (client default)' : 'Effective model',
    body: lines.join('<br>'),
  };
}

export interface TaskTagChip {
  id: string;
  label: string;
  color: string;
  ghost: boolean;
  concern: boolean;
  unparseable: boolean;
  tooltip: string;
}

const SUPPRESSED_CARD_TAG_TEXT = new Set([
  'ready',
  'reviewed',
  'reviewready',
  'readytosignoff',
  'autoreview',
  'autoreviewing',
  'humanreview',
  'concern',
  'concerns',
  'classifier',
  'classifierunknown',
  'classification',
  'orchestratormove',
  'orchestratormoved',
  'movedbyorchestrator',
  'qas',
  'qandas',
  'questionsandanswers',
]);

const LANE_MIRROR_CARD_TAG_TEXT: Record<string, readonly string[]> = {
  [TaskState.Backlog]: ['backlog'],
  [TaskState.Preparation]: ['preparation', 'prep'],
  [TaskState.OrchestratorPrep]: ['orchestratorprep', 'orchestratorpreparation', 'prep'],
  [TaskState.Ready]: ['ready'],
  [TaskState.Progress]: ['progress', 'inprogress'],
  [TaskState.FailedPickup]: ['failedpickup', 'pickupfailed'],
  [TaskState.AutoReview]: ['autoreview', 'review'],
  '4-review': ['review', 'autoreview'],
  [TaskState.HumanReview]: ['humanreview', 'review', 'reviewready', 'readytosignoff'],
  [TaskState.Escalated]: ['escalated', 'escalate', 'humanreview', 'decisionneeded'],
  [TaskState.Completed]: ['completed', 'complete', 'done'],
  [TaskState.Archive]: ['archive', 'archived'],
};

const HISTORY_TAG_RE = /^(?:reissue|abort-review):(.+)$/i;
const HISTORY_PRESENTATION_LANES = new Set<string>([
  TaskState.HumanReview,
  TaskState.Completed,
  TaskState.Archive,
]);

function compactTagText(value: string): string {
  return value
    .trim()
    .toLowerCase()
    .replace(/&/g, 'and')
    .replace(/[^a-z0-9]+/g, '');
}

function isSuppressedCardTag(id: string, entry: TagRegistryEntry | undefined, state: string | undefined): boolean {
  if (/^[a-z][a-z0-9-]*:(?:concerns?|unparseable)$/i.test(id)) return true;
  // The quality grade has its own prominent badge ({@link buildCodeReviewGradeBadge});
  // suppress the raw `code-review:grade-*` tag so it does not also render as a
  // dull chip in the tag row.
  if (CODE_REVIEW_GRADE_TAG_RE.test(id)) return true;
  const compactId = compactTagText(id);
  const compactLabel = entry ? compactTagText(entry.label) : '';
  // Internal transaction recovery marker. The computed integration badge is
  // the only card-facing source for integration truth.
  if (compactId === 'integrationpending' || compactLabel === 'integrationpending') return true;
  if (SUPPRESSED_CARD_TAG_TEXT.has(compactId) || SUPPRESSED_CARD_TAG_TEXT.has(compactLabel)) return true;
  const laneMirrors = state ? LANE_MIRROR_CARD_TAG_TEXT[state] ?? [] : [];
  return laneMirrors.includes(compactId) || (compactLabel.length > 0 && laneMirrors.includes(compactLabel));
}

/**
 * Tag chips on the card. Looks up label + colour from the workspace registry
 * map; tags whose id no longer exists render as a faint "ghost" chip with the
 * raw id so the user knows to clean up.
 */
export function buildTagChips(
  ids: readonly string[] | undefined,
  byId: Map<string, TagRegistryEntry>,
  state?: string,
): TaskTagChip[] {
  const list = ids ?? [];
  if (list.length === 0) return [];
  return list.flatMap((id) => {
    const entry = byId.get(id);
    if (isSuppressedCardTag(id, entry, state)) return [];
    // Reissue/abort tags are event history, not current card status.
    if (HISTORY_TAG_RE.test(id)) return [];
    if (entry) {
      return {
        id,
        label: entry.label,
        color: entry.color,
        ghost: false,
        concern: false,
        unparseable: false,
        tooltip: entry.description ? `${entry.label}: ${entry.description}` : entry.label
      };
    }
    return {
      id,
      label: id,
      color: '#475569',
      ghost: true,
      concern: false,
      unparseable: false,
      tooltip: `Unknown tag '${id}'; registry entry was removed`
      };
  });
}

export interface OwnerChip {
  id: string;
  label: string;
  initials: string;
  emoji: string;
  background: string;
  border: string;
  foreground: string;
  tooltip: string;
}

function tintFromHex(hex: string, alpha: number): string {
  const m = /^#([0-9a-f]{3}|[0-9a-f]{6})$/i.exec(hex.trim());
  if (!m) return `rgba(100,116,139,${alpha})`;
  let body = m[1];
  if (body.length === 3) body = body.split('').map(ch => ch + ch).join('');
  const r = parseInt(body.slice(0, 2), 16);
  const g = parseInt(body.slice(2, 4), 16);
  const b = parseInt(body.slice(4, 6), 16);
  return `rgba(${r},${g},${b},${alpha})`;
}

function ownerInitials(label: string, id: string): string {
  const words = label
    .trim()
    .split(/[^\p{L}\p{N}]+/u)
    .filter(Boolean);
  const source = words.length >= 2
    ? `${words[0][0]}${words[1][0]}`
    : (words[0] ?? id).slice(0, 2);
  return (source || '??').toUpperCase();
}

/** Owner-attribution chip: compact user marker + the owner's chosen colour. */
export function buildOwnerChip(owner: ClientSummary): OwnerChip {
  const baseColour = owner.colour || '#64748b';
  const label = owner.displayName || owner.id;
  return {
    id: owner.id,
    label,
    initials: ownerInitials(label, owner.id),
    emoji: owner.emoji || '·',
    background: tintFromHex(baseColour, 0.12),
    border: tintFromHex(baseColour, 0.32),
    foreground: '#e2e8f0',
    tooltip: `Owner: ${label} (${owner.id})`
  };
}

export type GitStateBadgeKind = 'pre-merge' | 'post-merge' | 'tagged';

export interface GitStateBadge {
  kind: GitStateBadgeKind;
  label: string;
  glyph: string;
  tooltip: string;
}

// Lane -> which cards may carry a git-state pill. Early lanes still stay quiet
// unless a real branch/tip is recorded; this lets prepared/ready worktree tasks
// show their branch before the first commit without inventing context for
// ordinary backlog cards. The lane no longer decides the LABEL: that comes from
// the provenance ground truth below. Completed/Archive still map straight
// through because the lane itself is the terminal fact (accepted into develop /
// archived).
const GIT_STATE_LANES: ReadonlySet<string> = new Set<string>([
  TaskState.Backlog,
  TaskState.Preparation,
  TaskState.OrchestratorPrep,
  TaskState.Ready,
  TaskState.Progress,
  TaskState.CodeNotComplete,
  TaskState.FailedPickup,
  TaskState.AutoReview,
  TaskState.HumanReview,
  TaskState.Escalated,
  TaskState.Completed,
  TaskState.Archive,
]);

const EARLY_GIT_CONTEXT_LANES: ReadonlySet<string> = new Set<string>([
  TaskState.Backlog,
  TaskState.Preparation,
  TaskState.OrchestratorPrep,
  TaskState.Ready,
  TaskState.FailedPickup,
]);

// Review lanes a PARALLEL run only reaches AFTER its task/<id> worktree was
// auto-integrated into develop and torn down (ADR-0052 / ASS-1731-1732). A card
// sitting here therefore has NO live worktree: its work already landed. A
// SEQUENTIAL run reaches the same lanes with no task branch at all, so we still
// gate the "landed" read on a branch having existed (a recorded branchTip).
const POST_INTEGRATION_REVIEW_LANES: ReadonlySet<string> = new Set<string>([
  TaskState.AutoReview,
  TaskState.HumanReview,
]);

function shortSha(sha: string): string {
  return sha.length > 7 ? sha.slice(0, 7) : sha;
}

/**
 * The CURRENT attempt's `task/<id>` tip: the newest recorded transition that
 * carries a non-null `branchTip`. Walking from the end makes a reissue point at
 * the live worktree, not an earlier run's tip. Null when no transition ever saw
 * a branch (a sequential run in the shared main checkout never cuts one).
 */
function currentBranchTip(prov: TaskInfo['provenance']): string | null {
  const transitions = prov?.transitions;
  if (!transitions?.length) return null;
  for (let i = transitions.length - 1; i >= 0; i--) {
    const tip = transitions[i]?.branchTip;
    if (tip) return tip;
  }
  return null;
}

/** Historical merge attempt SHA used only for pre-accept worktree context. */
function recordedMergeSha(prov: TaskInfo['provenance']): string | null {
  const sha = prov?.merge?.mergeCommit;
  return sha && sha.trim().length > 0 ? sha : null;
}

/**
 * Git-state badge (ASS-1665, reworked for ASS-1752). Accepted cards derive
 * target-branch location only from `job.integration`; earlier lanes use
 * provenance to describe their active worktree context:
 *
 *  1. Active worktree — a `task/<id>` branch exists (newest transition has a
 *     `branchTip`) and is not yet integrated. Names the branch + current-attempt
 *     tip, so a reissue tracks the live worktree.
 *  2. Landed in develop - computed attributed-commit membership for accepted
 *     cards, or legacy pre-accept integration context.
 *  3. Shared main checkout - a sequential run with no task branch at all. Says so
 *     instead of inventing a `task/<id>` that was never cut.
 *
 * Archived cards collapse to a quiet `tagged` pill. The three kinds keep the
 * existing pre-merge / post-merge / tagged styling.
 */
export function buildGitStateBadge(job: TaskInfo): GitStateBadge | null {
  if (!GIT_STATE_LANES.has(job.state)) return null;

  if (job.state === TaskState.Archive) {
    return {
      kind: 'tagged',
      label: 'tagged',
      glyph: '🏷',
      tooltip:
        'Git state: archived. The computed integration badge shows current target-branch membership.',
    };
  }

  const prov = job.provenance ?? null;
  const branchName = prov?.branch || `task/${job.key || job.id}`;
  const tip = currentBranchTip(prov);
  const mergeSha = recordedMergeSha(prov);
  const canonicalIntegration = currentIntegrationStatus(job);
  const usesCanonicalIntegration = INTEGRATION_STATUS_LANES.has(job.state);
  const deliveryRef = usesCanonicalIntegration
    ? canonicalIntegration?.deliveryRef?.trim() || null
    : null;

  if (EARLY_GIT_CONTEXT_LANES.has(job.state) && !tip && !mergeSha) {
    return null;
  }

  // Accepted cards use only the target-branch membership projection. Earlier
  // lanes retain the worktree lifecycle fallback because they have no
  // integration projection yet.
  const landed = usesCanonicalIntegration
    ? canonicalIntegration?.status === 'integrated'
    : !!mergeSha || (POST_INTEGRATION_REVIEW_LANES.has(job.state) && !!tip);
  if (landed) {
    const landedSha = usesCanonicalIntegration ? canonicalIntegration?.sha : mergeSha;
    const landedBranch = usesCanonicalIntegration
      ? canonicalIntegration?.integrationBranch || 'develop'
      : 'develop';
    const label = landedSha ? `${landedBranch} @${shortSha(landedSha)}` : landedBranch;
    return {
      kind: 'post-merge',
      label,
      glyph: '⬇',
      tooltip: landedSha
        ? `Git state: attributed commits are present in ${landedBranch} at ${shortSha(landedSha)}.`
        : `Git state: attributed commits are present in ${landedBranch}.`,
    };
  }

  // Accepted remote deliveries and settled local task branches use the same
  // backend-projected ref. This deliberately precedes the local branch-tip
  // heuristic so runner/<host>/<KEY> never falls through to "main checkout".
  if (deliveryRef) {
    return {
      kind: 'pre-merge',
      label: deliveryRef,
      glyph: '⎇',
      tooltip: `Git state: delivery ref ${deliveryRef} exists and is not yet fully integrated into ${canonicalIntegration?.integrationBranch || 'develop'}.`,
    };
  }

  // (1) Active worktree run. A real task/<id> branch exists and is not yet
  // integrated. The tip is the CURRENT attempt's, so a reissue tracks the live
  // worktree rather than an earlier run.
  if (tip) {
    return {
      kind: 'pre-merge',
      label: branchName,
      glyph: '⎇',
      tooltip: `Git state: pre-merge — this task's work lives in its own ${branchName} worktree (current run, tip ${shortSha(tip)}) and is not yet integrated into develop.`,
    };
  }

  // (3) Sequential run in the shared main checkout: no task/<id> worktree was
  // ever cut. Say so instead of showing a branch that does not exist.
  return {
    kind: 'pre-merge',
    label: 'main checkout',
    glyph: '✎',
    tooltip:
      "Git state: pre-merge — this is a sequential run working directly in the shared main checkout; no isolated task/<id> worktree was created. Its work is not yet integrated into develop.",
  };
}

/**
 * AGT-2046 — the always-on two-segment merge signal shown on every board card
 * that carries git work: "gemerged in develop / gemerged in main". The operator
 * scans the board for these two facts, so the card renders a compact
 * `[d|m]` indicator whose segments read filled/green when merged and muted/empty
 * when not.
 *
 * Main membership comes from the backend-computed
 * {@link TaskInfo.mergeSignal}. On accepted cards the develop segment is always
 * overlaid from {@link TaskInfo.integration}, including when the merge signal is
 * stale or absent. Lane and provenance facts never substitute for membership.
 *
 * Semantics + colours match the detail-header landed-state (ASS-1724 / AGT-1989):
 * develop and main are the same worktree -> develop -> main ladder rungs.
 */
export interface MergeSignalSegment {
  key: 'develop' | 'main';
  /** One-letter scan glyph: `d` (develop) / `m` (main). */
  short: 'd' | 'm';
  /** Full branch label for the tooltip ("develop" / "main"). */
  label: string;
  merged: boolean;
  /** Short SHA that proves the membership, when known. */
  sha: string | null;
}

export interface MergeSignalView {
  branch: string | null;
  develop: MergeSignalSegment;
  main: MergeSignalSegment;
  /** Display segments; identical integration and release targets collapse to one. */
  segments: readonly MergeSignalSegment[];
  /** Plain-text tooltip: branch + merge-target status in Klartext. */
  tooltip: string;
  /** Compact aria label for screen readers ("in develop, not in main"). */
  ariaLabel: string;
}

const INTEGRATION_STATUS_LANES = new Set<string>([
  TaskState.HumanReview,
  TaskState.Completed,
  TaskState.Archive,
]);

/** Defensive lane gate for a read-time integration overlay from an older poll. */
export function currentIntegrationStatus(job: TaskInfo): TaskInfo['integration'] {
  return INTEGRATION_STATUS_LANES.has(job.state) ? job.integration ?? null : null;
}

function shortShaOf(sha: string | null | undefined): string | null {
  if (!sha) return null;
  const s = sha.trim();
  if (s.length === 0) return null;
  return s.length > 7 ? s.slice(0, 7) : s;
}

/**
 * True when the card has at least one attributed TASK commit (the SSOT
 * {@link commitChainOf}). The merge signal is a statement about the task's own
 * commits, so this is the whole gate. AGT-2063: a card that committed nothing
 * gets NO signal - not from the backend `mergeSignal`, not from a recorded
 * branch tip, not from a merge fact. A `task/<id>` branch that was cut but never
 * produced a commit has its base commit as the tip, and the base is trivially an
 * ancestor of develop/main; keying off it painted commit-less cards as "in
 * develop", the false statement the operator hit.
 */
function hasTaskCommits(job: TaskInfo): boolean {
  return commitChainOf(job).length > 0;
}

/**
 * Build the two-segment merge signal for a card. Returns null when the card has
 * no attributed task commit to describe: a card without commits shows no signal
 * at all, not an empty or default one (AGT-2063).
 */
export function buildMergeSignal(job: TaskInfo): MergeSignalView | null {
  if (!hasTaskCommits(job)) return null;

  const sig = job.mergeSignal ?? null;
  const prov = job.provenance ?? null;
  const canonicalIntegration = currentIntegrationStatus(job);
  const usesCanonicalIntegration = INTEGRATION_STATUS_LANES.has(job.state);

  let inDevelop: boolean;
  let inMain: boolean;
  let developSha: string | null;
  let mainSha: string | null;
  let branch: string | null;
  let integrationLabel: string;
  let releaseLabel: string;

  if (sig) {
    inDevelop = usesCanonicalIntegration
      ? canonicalIntegration?.status === 'integrated'
      : sig.inIntegration;
    inMain = sig.inRelease;
    developSha = usesCanonicalIntegration
      ? inDevelop ? canonicalIntegration?.sha ?? null : null
      : sig.integrationSha ?? null;
    mainSha = sig.releaseSha ?? null;
    branch = sig.branch || prov?.branch || null;
    integrationLabel = canonicalIntegration?.integrationBranch || sig.integrationBranch || 'develop';
    releaseLabel = sig.releaseBranch || 'main';
  } else {
    // Conservative degradation: only the canonical integration projection can
    // prove target-branch membership. Lane and provenance never substitute.
    inDevelop = canonicalIntegration?.status === 'integrated';
    inMain = false;
    developSha = inDevelop ? shortShaOf(canonicalIntegration?.sha) : null;
    mainSha = null;
    branch = prov?.branch || null;
    integrationLabel = canonicalIntegration?.integrationBranch || 'develop';
    releaseLabel = 'main';
  }

  const develop: MergeSignalSegment = {
    key: 'develop',
    short: 'd',
    label: integrationLabel,
    merged: inDevelop,
    sha: developSha,
  };
  const main: MergeSignalSegment = {
    key: 'main',
    short: 'm',
    label: releaseLabel,
    merged: inMain,
    sha: mainSha,
  };

  const sameTarget = integrationLabel.trim() === releaseLabel.trim();
  const unifiedTarget: MergeSignalSegment = {
    ...main,
    merged: inDevelop || inMain,
    sha: mainSha ?? developSha,
  };
  const segments = sameTarget ? [unifiedTarget] : [develop, main];

  const developLine = inDevelop
    ? developSha
      ? `In ${integrationLabel} since ${developSha}`
      : `In ${integrationLabel}`
    : `Not yet in ${integrationLabel}`;
  const mainLine = inMain
    ? mainSha
      ? `In ${releaseLabel} (${mainSha})`
      : `In ${releaseLabel}`
    : `Not in ${releaseLabel}`;

  const unifiedLine = unifiedTarget.merged
    ? unifiedTarget.sha
      ? `In ${unifiedTarget.label} (${unifiedTarget.sha})`
      : `In ${unifiedTarget.label}`
    : `Not yet in ${unifiedTarget.label}`;
  const tooltip = [
    branch ? `Branch: ${branch}` : null,
    'Merge status:',
    ...(sameTarget ? [`• ${unifiedLine}`] : [`• ${developLine}`, `• ${mainLine}`]),
  ].filter((line): line is string => line !== null).join('\n');

  const ariaLabel = sameTarget
    ? `Merge status: ${unifiedTarget.merged ? 'in' : 'not in'} ${unifiedTarget.label}`
    : `Merge status: ${inDevelop ? `in ${integrationLabel}` : `not in ${integrationLabel}`}, ` +
      `${inMain ? `in ${releaseLabel}` : `not in ${releaseLabel}`}`;

  return { branch, develop, main, segments, tooltip, ariaLabel };
}

export type PipelineDotStatus = 'done' | 'active' | 'pending' | 'blocked';

export interface PipelineDot {
  id: 'pre' | 'run' | 'post' | 'review';
  label: string;
  status: PipelineDotStatus;
}

export interface PipelineDotsView {
  dots: PipelineDot[];
  currentLabel: string | null;
  tooltip: string;
}

function pipelineView(
  current: PipelineDot['id'] | null,
  blocked: PipelineDot['id'] | null,
  doneThrough: PipelineDot['id'] | null,
): PipelineDotsView {
  const order: PipelineDot['id'][] = ['pre', 'run', 'post', 'review'];
  const labels: Record<PipelineDot['id'], string> = {
    pre: 'Pre steps',
    run: 'Core agent work',
    post: 'Post steps',
    review: 'Review',
  };
  const doneIndex = doneThrough ? order.indexOf(doneThrough) : -1;
  const dots = order.map((id, index): PipelineDot => ({
    id,
    label: labels[id],
    status: blocked === id ? 'blocked'
      : current === id ? 'active'
        : index <= doneIndex ? 'done'
          : 'pending',
  }));
  const currentLabel = current ? labels[current] : null;
  return {
    dots,
    currentLabel,
    tooltip: `Pipeline: ${dots.map((dot) => `${dot.label} ${dot.status}`).join(', ')}`,
  };
}

/**
 * Tiny card-level pipeline indicator. The board payload intentionally does not
 * carry the full per-task pipeline execution; that lives behind the detail
 * endpoint. The card therefore maps the existing lane/phase/execution signals
 * to the four visible sections (Pre steps, core agent work, post steps, review) without inventing
 * per-step results.
 */
export function buildPipelineDots(job: TaskInfo): PipelineDotsView {
  switch (job.phase ?? null) {
    case 'intake-running':
      return pipelineView('pre', null, null);
    case 'intake-blocked':
      return pipelineView(null, 'pre', null);
    case 'intake-passed':
      return pipelineView(null, null, 'pre');
    case 'post-processing-running':
      return pipelineView('post', null, 'run');
    case 'post-processing-blocked':
      return pipelineView(null, 'post', 'run');
    case 'awaiting-review':
      return pipelineView(null, null, 'post');
  }

  if (job.state === TaskState.Progress) {
    if (job.execution?.status === 'running') {
      return pipelineView('run', null, 'pre');
    }
    return pipelineView(null, null, 'run');
  }

  if (job.state === TaskState.AutoReview || job.state === '4-review') {
    return pipelineView('review', null, 'post');
  }

  if (job.state === TaskState.HumanReview || job.state === TaskState.Escalated) {
    return pipelineView(null, null, 'review');
  }

  if (job.state === TaskState.Completed || job.state === TaskState.Archive) {
    return pipelineView(null, null, 'review');
  }

  return pipelineView(null, null, null);
}

export type PhaseBadgeTone =
  | 'human-ready'
  | 'intake-running'
  | 'intake-blocked'
  | 'intake-passed'
  | 'loop-waiting'
  | 'steer-pending'
  | 'post-processing-running'
  | 'post-processing-blocked'
  | 'awaiting-review'
  | 'integrating';
export interface PhaseBadge { label: string; tone: PhaseBadgeTone; tooltip: string; }

export interface QuotaWaitBadge { label: string; minutesLeft: number; tooltip: string; }

/** Visible Run-Liveness-style projection of the durable quota-wait marker. */
export function buildQuotaWaitBadge(wait: TaskInfo['quotaWait'], nowMs: number): QuotaWaitBadge | null {
  if (!wait?.resetAt) return null;
  const resetMs = Date.parse(wait.resetAt);
  if (!Number.isFinite(resetMs)) return null;
  const minutesLeft = Math.max(0, Math.ceil((resetMs - nowMs) / 60_000));
  const resetLabel = new Date(resetMs).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
  const remaining = minutesLeft > 0 ? `${minutesLeft} min remaining` : 'reset due · refreshing';
  return {
    label: `Waiting for quota reset ${resetLabel} · ${remaining}`,
    minutesLeft,
    tooltip: `${wait.reason}. The runner keeps this state visible and retries admission after refreshing ${wait.cliType} quota.`,
  };
}

/**
 * Format a steer wait as compact total-minutes `mm:ss`. The card keeps this
 * established long-wait representation while lifecycle labels elsewhere use
 * the shared hour-aware formatter.
 */
export function formatSteerWait(elapsedMs: number): string {
  const total = Math.max(0, Math.floor(elapsedMs / 1000));
  const minutes = Math.floor(total / 60);
  const seconds = total % 60;
  return `${minutes}:${String(seconds).padStart(2, '0')}`;
}

/**
 * Card-pill tone + tooltip per lifecycle phase. Only the card-specific
 * presentation lives here; the visible label text comes from the shared
 * PHASE_LABELS source of truth via {@link phaseStaticLabel} so the pill and the
 * task-detail chip can never disagree on wording. Phases absent from this table
 * (human-ready, execution-running, execution-stalled) render no pill on the
 * card: the lane already says "Ready" and a live execution shows the
 * "Running live" chip instead.
 */
const PHASE_PILL: Partial<Record<string, { tone: PhaseBadgeTone; tooltip: string }>> = {
  'steer-pending': { tone: 'steer-pending',
    tooltip: 'The run asked a question and is waiting for an answer. If it stays unanswered it is auto-answered from the task context or escalated - it will not hang (Run-Liveness Slice B).' },
  'loop-waiting': { tone: 'loop-waiting',
    tooltip: 'The coding CLI has exited and freed its execution slot. The orchestrator is preparing a continuation, which must acquire a new slot before the CLI resumes.' },
  'intake-running': { tone: 'intake-running',
    tooltip: 'Orchestrator intake is checking this card (separate runner from the coding CLI).' },
  'intake-blocked': { tone: 'intake-blocked',
    tooltip: 'Orchestrator intake flagged this card. Check the activity log for the reason and resolve before the coding runner can pick it up.' },
  'intake-passed': { tone: 'intake-passed',
    tooltip: 'Orchestrator intake approved this card. The coding runner is now allowed to pick it up.' },
  'post-processing-running': { tone: 'post-processing-running',
    tooltip: 'The coding CLI has finished. An orchestrator or supporting agent is running post-processing before review.' },
  'post-processing-blocked': { tone: 'post-processing-blocked',
    tooltip: 'Orchestrator post-processing needs a human decision or failed before it could pass this task to review.' },
  'awaiting-review': { tone: 'awaiting-review',
    tooltip: 'Post-processing finished and the task is waiting for the review transition.' },
  'integrating': { tone: 'integrating',
    tooltip: 'Acceptance is integrating the reviewed delivery. The task stays in Review until integration succeeds.' },
};

/** The two intentional-wait phases whose pill carries a live "since m:ss" timer. */
const TIMED_WAIT_PHASES = new Set(['loop-waiting', 'steer-pending']);

/**
 * Lifecycle-phase chip. Surfaces the `phase` substate on cards that carry one.
 * Returns null when the job has no explicit phase (or a phase the card renders
 * elsewhere), so cards that predate the field render exactly like before. For
 * the two intentional-wait phases the optional `steerPendingSince` + `nowMs`
 * append the "since m:ss" timer (Run-Liveness Slices B/C).
 */
export function buildPhaseBadge(
  phase: TaskInfo['phase'],
  steerPendingSince?: string | null,
  nowMs?: number,
  state?: string,
): PhaseBadge | null {
  if (state && HISTORY_PRESENTATION_LANES.has(state) && phase !== 'integrating') return null;
  if (!phase) return null;
  const pill = PHASE_PILL[phase];
  if (!pill) return null;
  let label = phaseStaticLabel(phase) ?? phase;
  if (TIMED_WAIT_PHASES.has(phase)) {
    const since = steerPendingSince ? Date.parse(steerPendingSince) : NaN;
    if (Number.isFinite(since) && typeof nowMs === 'number') {
      const separator = phase === 'steer-pending' ? ' · ' : ' ';
      label = `${label}${separator}${formatSteerWait(nowMs - since)}`;
    }
  }
  return { label, tone: pill.tone, tooltip: pill.tooltip };
}

export interface ExecutionBadge { label: string; tone: 'running' | 'failed' | 'cancelled'; }

export function buildExecutionBadge(job: TaskInfo): ExecutionBadge | null {
  // Lane wins over execution-status. The backend overlay already clears
  // Execution for non-progress tasks (TaskEndpointHelpers.WithRuntime), but a
  // stale poll snapshot or an optimistic move can briefly land on the card
  // before the next round-trip. Without this guard, a card in 4-auto-review /
  // 5-human-review can flash "Running live" while the task is not actually
  // executing in this lane.
  if (job.state !== TaskState.Progress) return null;

  // The pipeline overlay is newer than the runner/execution overlays. A live
  // pre-step or between-step owner therefore wins over stale terminal CLI
  // state instead of flashing a false failure.
  if (isTaskRunActive(job)) {
    return { label: 'Running live', tone: 'running' };
  }

  const execution = job.execution;
  if (!execution) return null;

  if (shouldShowFailureToast(execution)) {
    return { label: execution.exitCode === null ? 'Failed' : `Failed (${execution.exitCode})`, tone: 'failed' };
  }

  if (execution.runOutcome === 'noop') {
    return { label: 'NoOp', tone: 'cancelled' };
  }

  if (execution.runOutcome === 'blocked') {
    return { label: 'Blocked', tone: 'cancelled' };
  }

  if (execution.runOutcome === 'needs-input') {
    return { label: 'Needs input', tone: 'cancelled' };
  }

  // 'stopped' is the new deliberate-kill status from the backend (user pause,
  // Pause-&-Send, watchdog kill). Render as a calm "Stopped" pill, not a
  // failure. Legacy 'cancelled' value stays supported so older in-memory
  // CliExecution records keep rendering.
  if (execution.status === 'stopped' || execution.status === 'cancelled') {
    return { label: 'Stopped', tone: 'cancelled' };
  }

  return null;
}

/**
 * DtC drive-to-conclusion infra-retry budget: up to 3 total attempts per
 * run-chain (attempt 1 = original run + up to 2 infra retries). Mirrors the
 * backend `CompletionRetrigger` DefaultBudget; see the four-terminal model in
 * `docs/concepts/orchestrator-drive-to-conclusion.html`. The k/3 in the
 * CooldownRetry banner counts against this budget.
 */
export const INFRA_RETRY_BUDGET = 3;

export interface CooldownRetryBanner {
  /** Attempt this cooldown is holding for, clamped to [1, budget]. */
  attempt: number;
  budget: number;
  /** Whole seconds until the scheduled re-pickup, or null when already due. */
  secondsLeft: number | null;
  /** Primary line, e.g. `infra-crashed · retrying 2/3`. */
  label: string;
  /** Countdown fragment, e.g. `in 210s` (or `now` when the timer elapsed). */
  countdown: string;
  tooltip: string;
}

/**
 * DtC step 6 — the CooldownRetry banner for a `3-progress` card that infra-crashed
 * and is holding out a scheduled re-pickup backoff (the `runActivity.failed-backoff`
 * state, ASS-1751). This is the ONLY non-live state allowed in 3-progress, and it
 * must read distinctly from the normal "Running live" chip so a cooling task does
 * not look like a fresh stall: the card renders it as a warn-toned banner
 * (`infra-crashed · retrying k/3 · in Ns`), not the running tint.
 *
 * Source is the already-overlaid `runActivity` (kind + backoffUntil + attempt) —
 * no new side-channel. `nowMs` is injected so the countdown ticks from the card's
 * shared clock signal. Returns null off the Progress lane, when no runActivity is
 * attached, or for any run-activity state other than `failed-backoff`.
 */
export function buildCooldownRetryBanner(job: TaskInfo, nowMs: number): CooldownRetryBanner | null {
  if (job.state !== TaskState.Progress) return null;
  const activity = job.runActivity;
  if (!activity || activity.kind !== 'failed-backoff') return null;

  const attempt = Math.min(Math.max(activity.attempt, 1), INFRA_RETRY_BUDGET);
  const untilMs = activity.backoffUntil ? Date.parse(activity.backoffUntil) : Number.NaN;
  const secondsLeft = Number.isFinite(untilMs) && untilMs > nowMs
    ? Math.max(1, Math.round((untilMs - nowMs) / 1000))
    : null;
  const countdown = secondsLeft !== null ? `in ${secondsLeft}s` : 'now';

  const lastError = activity.lastError?.trim();
  const tooltipLines = [
    'Infra crash — the last run died before a terminal verdict.',
    `The orchestrator kept the loop and scheduled a re-pickup (attempt ${attempt} of ${INFRA_RETRY_BUDGET})${secondsLeft !== null ? ` in ~${secondsLeft}s` : ' now'}.`,
    'This is a held CooldownRetry, not a live run and not a stall.',
  ];
  if (lastError) tooltipLines.push(`Last error: ${lastError}`);

  return {
    attempt,
    budget: INFRA_RETRY_BUDGET,
    secondsLeft,
    label: `infra-crashed · retrying ${attempt}/${INFRA_RETRY_BUDGET}`,
    countdown,
    tooltip: tooltipLines.join('\n'),
  };
}

export interface ReviewBadge { label: string; tone: 'generating' | 'ready' | 'failed'; tooltip: string; }

/**
 * Post-run summary descriptor. The card stays quiet after the summary settles.
 */
export function buildReviewBadge(summaryState: TaskInfo['summaryState']): ReviewBadge | null {
  if (!summaryState) return null;
  switch (summaryState.status) {
    case 'generating':
      return { label: 'summarizing', tone: 'generating',
               tooltip: 'Orchestrator is summarizing the run output (Haiku). The card will become quiet once status.md has been written.' };
    case 'ready':
      return null;
    case 'failed':
      return { label: 'review failed', tone: 'failed',
               tooltip: summaryState.errorMessage ?? 'Auto-review failed.' };
    case 'degraded': {
      const attempts = summaryState.attempt && summaryState.maxAttempts
        ? ` after ${summaryState.attempt}/${summaryState.maxAttempts} summary attempts`
        : '';
      return { label: 'result degraded', tone: 'failed',
               tooltip: `Result summary degraded${attempts}. The completed core run remains reviewable. ${summaryState.errorMessage ?? ''}`.trim() };
    }
    default:
      return null;
  }
}

export interface AutoReviewProcessBadge {
  label: string;
  tone: 'active' | 'waiting' | 'gate-queued';
  tooltip: string;
}

/** Compact live-step or elapsed-wait descriptor for a Post Processing card. */
export function buildAutoReviewProcessBadge(job: TaskInfo, status: AutoReviewStatusView | null, nowMs: number): AutoReviewProcessBadge | null {
  if (job.state !== TaskState.AutoReview) return null;

  const activity = status?.activeJobs?.find(item =>
    item.jobId === job.id && item.project === job.projectName);
  const lifecycleStep = runningLifecycleStep(job);
  const matchesLegacyCurrent = !activity
    && !!status?.currentJob
    && status.currentJob === job.id
    && (!status.currentProject || status.currentProject === job.projectName);
  const phaseOnlyActive = !status && job.phase === 'post-processing-running';
  if (activity || matchesLegacyCurrent || lifecycleStep || phaseOnlyActive) {
    const step = activity?.step ?? lifecycleStep ?? (matchesLegacyCurrent ? 'aspects' : 'processing');
    const stepLabel = autoReviewStepLabel(step);
    if (step === 'gate-queued') {
      const queueStartedAt = Date.parse(activity?.startedAt ?? job.enteredLaneAt ?? job.lastActivity);
      return {
        label: `Gate queued ${formatAutoReviewWait(Number.isFinite(queueStartedAt) ? nowMs - queueStartedAt : 0)}`,
        tone: 'gate-queued',
        tooltip: 'This task has been admitted to post-processing and is waiting for the shared build/test machine lock.',
      };
    }
    return {
      label: stepLabel,
      tone: 'active',
      tooltip: `Post-processing is active for this task. Current step: ${stepLabel}.`,
    };
  }

  const enteredAt = Date.parse(job.enteredLaneAt ?? job.lastActivity);
  const wait = formatAutoReviewWait(Number.isFinite(enteredAt) ? nowMs - enteredAt : 0);
  return {
    label: `waiting ${wait}`,
    tone: 'waiting',
    tooltip: `Waiting for a post-processing slot since ${new Date(
      Number.isFinite(enteredAt) ? enteredAt : nowMs).toLocaleString()}.`,
  };
}

function runningLifecycleStep(job: TaskInfo): string | null {
  const running = job.postProcessingChecks?.find(check => check.status === 'running');
  if (!running) return null;
  const name = running.name.toLowerCase();
  if (name.includes('build') || name.includes('gate')) return 'gate';
  if (name.includes('aspect')) return 'aspects';
  if (name.includes('grade') || name.includes('code-review')) return 'grade';
  if (name.includes('decision')) return 'decision';
  return 'processing';
}

function autoReviewStepLabel(step: string): string {
  switch (step) {
    case 'gate': return 'Gate running';
    case 'aspects': return 'Aspects';
    case 'grade': return 'Grade';
    case 'decision': return 'Decision';
    default: return 'Processing';
  }
}

export function formatAutoReviewWait(elapsedMs: number): string {
  const totalMinutes = Math.max(0, Math.floor(elapsedMs / 60_000));
  if (totalMinutes < 60) return `${totalMinutes}m`;
  const hours = Math.floor(totalMinutes / 60);
  const minutes = totalMinutes % 60;
  return minutes > 0 ? `${hours}h ${minutes}m` : `${hours}h`;
}

export interface HumanReviewBadge { label: string; tone: 'attention'; tooltip: string; }

/** Decision-backlog impact is independent of the orchestrator review verdict. */
export function buildDecisionDamBadge(job: TaskInfo): HumanReviewBadge | null {
  const impact = job.transitiveWaiters;
  if (job.state !== TaskState.HumanReview || !impact || impact.count <= 0) return null;
  return {
    label: `Dams ${impact.count} ${impact.count === 1 ? 'card' : 'cards'}`,
    tone: 'attention',
    tooltip: `Transitive decision backlog (${impact.count}): ${impact.keys.join(', ')}. These cards wait directly or indirectly on this decision.`,
  };
}

/**
 * Acute decision badge. The current lane is authoritative: only a card that is
 * still in 5e-escalated may render "Escalated". A journal verdict on Review is
 * historical and stays in the timeline.
 */
export function buildHumanReviewBadge(job: TaskInfo): HumanReviewBadge | null {
  if (job.state !== TaskState.Escalated) return null;
  const lane = lanePresentation(TaskState.Escalated);
  return {
    label: lane.name,
    tone: 'attention',
    tooltip: `This task is currently in the ${lane.name} lane and needs an operator decision.`
  };
}

export interface RunnerBadge {
  /** `remote` renders the arrow + runner name; `local` renders the quiet "lokal" chip. */
  kind: 'remote' | 'local';
  /** Arrow glyph for the remote case; empty for local. */
  glyph: string;
  label: string;
  tooltip: string;
}

/**
 * AGT-2003 runner badge shown next to the CLI badge on a running card. A remote
 * runner acquires the task's run lease (ADR-0060) before it spawns its CLI, so
 * `job.runner.isRemote` means the work is executing on another host: render
 * "→ <runner-name>". A local in-process run holds no run lease (it uses the disk
 * pickup-lock), so a running Progress card with no remote lease renders a quiet
 * "lokal" chip — the operator's "Abgleich im Stable Board" (lokal vs remote).
 * Returns null on non-running cards so the board stays quiet everywhere else.
 *
 * Recognizable-pattern sibling of the git-state / branch-context signal
 * (AGT-1984): a glyph + short label chip that reads at a glance.
 */
export function buildRunnerBadge(job: TaskInfo): RunnerBadge | null {
  const running = job.state === TaskState.Progress && job.execution?.status === 'running';
  const runner = job.state === TaskState.Progress ? job.runner ?? null : null;

  if (runner && runner.isRemote) {
    const name = (runner.runnerName || runner.runnerId || 'remote runner').trim();
    const host = (runner.hostname || '').trim();
    const parts = [`Running remotely on ${name}${host ? ` (${host})` : ''}.`];
    parts.push('This task is running on another host, not in-process (holds the run lease).');
    return { kind: 'remote', glyph: '⇥', label: `remote · ${name}`, tooltip: parts.join('\n') };
  }

  // Local: only assert "lokal" while the card is genuinely running in-process
  // (or a same-backend lease is held). Nothing to show once it stops.
  if (running || runner) {
    return {
      kind: 'local',
      glyph: '',
      label: 'lokal',
      tooltip: 'Running in-process on the local backend (no remote run lease held).',
    };
  }
  return null;
}

export interface ExternalDoneBadge { label: string; tooltip: string; }

/**
 * "extern erledigt" badge for a task completed out-of-band (operator chat,
 * external agent, remote host) and reconciled through the external-completion
 * endpoint. Renders next to the CLI/model chip so a card whose work happened
 * outside the runner reads as intentionally done, not abandoned. See
 * docs/concepts/out-of-band-task-completion.md §3.
 */
export function buildExternalDoneBadge(job: TaskInfo): ExternalDoneBadge | null {
  const ext = job.externalCompletion;
  if (!ext) return null;
  const source = (ext.source ?? '').trim() || 'external';
  const when = formatExternalCompletionDate(ext.completedAt);
  const summary = (ext.summary ?? '').trim();
  const parts = [`Completed out-of-band by ${source}${when ? ` on ${when}` : ''}.`];
  if (summary) parts.push(summary);
  parts.push('Reconciled via the external-completion endpoint; see results/deliverables.md.');
  return { label: 'extern erledigt', tooltip: parts.join('\n\n') };
}

function formatExternalCompletionDate(iso: string | null | undefined): string {
  if (!iso) return '';
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? '' : d.toLocaleDateString();
}

/** Host-level attention follows the current acute lane, never an old verdict. */
export function cardNeedsAttention(job: TaskInfo): boolean {
  return job.state === TaskState.Escalated;
}

/** Matches the per-task quality-grade tag the automatic code-review step hangs
 *  (`code-review:grade-a` .. `code-review:grade-d`). */
const CODE_REVIEW_GRADE_TAG_RE = /^code-review:grade-([abcd])$/i;

export type CodeReviewGradeLetter = 'A' | 'B' | 'C' | 'D';

export interface CodeReviewGradeBadge {
  grade: CodeReviewGradeLetter;
  /** Lower-case letter, drives the per-grade colour class on the badge. */
  tone: 'a' | 'b' | 'c' | 'd';
  tooltip: string;
}

const CODE_REVIEW_GRADE_MEANING: Record<CodeReviewGradeLetter, string> = {
  A: 'Solves the goal clearly — complete, coherent, and backed by tests / evidence.',
  B: 'Solid work that does the job, with small gaps a human can accept.',
  C: 'Concerns — the work is half-done or unclear; a reviewer would want changes before shipping.',
  D: 'Misses the goal, redundantly redoes existing behaviour, or leaves broken / half-finished work.',
};

/**
 * Quality-grade badge (ASS-1657). Every task that runs through the pipeline
 * gets an automatic A/B/C/D code-review grade, carried as a
 * `code-review:grade-{a-d}` tag. This lifts the grade out of the tag row into a
 * prominent, colour-coded card badge so the operator reads the quality verdict
 * at a glance. Returns null when no grade tag is present (older cards, or a task
 * that has not reached the grade step yet).
 */
export function buildCodeReviewGradeBadge(tags: readonly string[] | undefined): CodeReviewGradeBadge | null {
  for (const id of tags ?? []) {
    const m = CODE_REVIEW_GRADE_TAG_RE.exec(id);
    if (m) {
      const tone = m[1].toLowerCase() as 'a' | 'b' | 'c' | 'd';
      const grade = tone.toUpperCase() as CodeReviewGradeLetter;
      return {
        grade,
        tone,
        tooltip: `Quality grade ${grade} — automatic code-review step. ${CODE_REVIEW_GRADE_MEANING[grade]}`,
      };
    }
  }
  return null;
}

export interface OutcomeIssueBadge { label: string; tone: 'info' | 'warn' | 'high'; tooltip: string; }

const CURRENT_OUTCOME_ISSUE_LANES = new Set<string>([
  TaskState.Progress,
  TaskState.FailedPickup,
  TaskState.CodeNotComplete,
  TaskState.AutoReview,
  TaskState.Escalated,
]);
const SUCCESSFUL_RUN_OUTCOMES = new Set(['success', 'noop']);
const INTEGRATION_ISSUE_KINDS = new Set(['integration-error', 'integration-conflict']);

export function buildOutcomeIssueBadge(job: TaskInfo): OutcomeIssueBadge | null {
  const issue = job.outcomeIssue;
  if (!issue) return null;
  const isActivePrepareBackoff = job.state === TaskState.Ready
    && issue.kind.toLowerCase() === 'worktree-preparation-failed';
  if (!CURRENT_OUTCOME_ISSUE_LANES.has(job.state) && !isActivePrepareBackoff) return null;
  const runOutcome = (job.execution?.runOutcome ?? '').toLowerCase();
  if (SUCCESSFUL_RUN_OUTCOMES.has(runOutcome)) return null;
  if (job.integration?.status === 'integrated'
      && INTEGRATION_ISSUE_KINDS.has(issue.kind.toLowerCase())) return null;
  const severity = (issue.severity ?? '').toLowerCase();
  const tone = severity === 'high' ? 'high' : severity === 'warn' ? 'warn' : 'info';
  const seen = issue.lastSeenAt ? `\nLast seen: ${formatShortTime(issue.lastSeenAt)}` : '';
  const summary = issue.summary ? `\n\n${issue.summary}` : '';
  return {
    label: issue.label || issue.kind,
    tone,
    tooltip: `Runner outcome issue: ${issue.kind}${seen}${summary}`
  };
}

/**
 * AGT-2029 waits-on dependency chip. Consumes the backend-computed
 * `waitsOn` status (fulfilled/open per target, blocked, cycle) so the card
 * shows what the task is waiting on, in which state, and can route to the
 * target - matching the scheduler's own decision (the runner uses the same
 * evaluation to gate auto-pickup). Null when the task has no dependencies.
 *
 * States: `open` (⏳, at least one dependency is awaiting completion or an
 * explicit release - the card is held back from auto-pickup), `ready` (✓, all complete - the card is
 * workable), `cycle` (⚠, a dependsOn cycle that can never be fulfilled -
 * a configuration error).
 */
export interface DependencyChip {
  glyph: string;
  label: string;
  tone: 'open' | 'ready' | 'cycle';
  tooltip: string;
  /** F33 key the chip navigates to on click (first open target, else the first). */
  targetKey: string | null;
  /** Direct nav target resolved by the backend (works across lanes/projects, incl. archive). */
  targetJobId: string | null;
  targetWatchPath: string | null;
}

export function buildDependencyChip(waitsOn: TaskInfo['waitsOn']): DependencyChip | null {
  if (!waitsOn || waitsOn.items.length === 0) return null;
  const items = waitsOn.items;
  const tooltip = dependencyTooltip(items, waitsOn.cycleDetected);

  if (waitsOn.cycleDetected) {
    const primary = items[0];
    return {
      glyph: '⚠',
      label: 'dep cycle',
      tone: 'cycle',
      tooltip,
      targetKey: primary?.key ?? null,
      targetJobId: primary?.targetJobId ?? null,
      targetWatchPath: primary?.targetWatchPath ?? null,
    };
  }

  const open = items.filter((i) => !i.fulfilled);
  if (open.length > 0) {
    const primary = open[0];
    const extra = open.length - 1;
    const waitReason = primary.waitingForRelease ? 'release' : 'completion';
    return {
      glyph: '⏳',
      label: `waits for ${waitReason}: ${primary.key}${extra > 0 ? ` +${extra}` : ''}`,
      tone: 'open',
      tooltip,
      targetKey: primary.key,
      targetJobId: primary.targetJobId ?? null,
      targetWatchPath: primary.targetWatchPath ?? null,
    };
  }

  const primary = items[0];
  const extra = items.length - 1;
  return {
    glyph: '✓',
    label: `${primary.key}${extra > 0 ? ` +${extra}` : ''}`,
    tone: 'ready',
    tooltip,
    targetKey: primary.key,
    targetJobId: primary.targetJobId ?? null,
    targetWatchPath: primary.targetWatchPath ?? null,
  };
}

/**
 * TaskLiveStatus owns the one CURRENT wait reason on current payloads. Keep the
 * dependency chip only as a fallback for older payloads without liveStatus.
 */
export function buildVisibleDependencyChip(job: TaskInfo): DependencyChip | null {
  if (job.liveStatus) return null;
  return buildDependencyChip(job.waitsOn);
}

/**
 * AGT-2029: resolve the task a dependency chip should open. Prefers the
 * backend-resolved target (jobId + watchPath — correct across projects and
 * lanes the board snapshot omits, e.g. an archived target), falling back to the
 * F33 key against the current board snapshot. Null when it is not loaded.
 */
export function resolveDependencyTarget(
  chip: DependencyChip,
  jobs: readonly TaskInfo[],
): TaskInfo | null {
  if (chip.targetJobId) {
    const byId = jobs.find(
      (t) => t.id === chip.targetJobId && (!chip.targetWatchPath || t.watchPath === chip.targetWatchPath),
    );
    if (byId) return byId;
  }
  if (chip.targetKey) {
    const upper = chip.targetKey.toUpperCase();
    const byKey = jobs.find((t) => (t.key ?? '').toUpperCase() === upper);
    if (byKey) return byKey;
  }
  return null;
}

function dependencyTooltip(
  items: NonNullable<TaskInfo['waitsOn']>['items'],
  cycle: boolean,
): string {
  const lines = items.map((i) => {
    const mark = i.fulfilled ? '✓' : '◦';
    const state = i.fulfilled
      ? 'done'
      : i.waitingForRelease
        ? 'completed, release pending'
        : i.resolved
          ? 'completion pending'
          : 'not created yet';
    const title = i.targetTitle ? ` — ${i.targetTitle.slice(0, 40)}` : '';
    return `${mark} ${i.key} (${state})${title}`;
  });
  const head = cycle
    ? 'Dependency cycle: this task can never be auto-picked until the chain is fixed via its references.'
    : items.every((i) => i.fulfilled)
      ? 'All dependencies complete — this task is workable.'
      : 'Waiting for dependency completion or explicit release before pickup:';
  return `${head}\n${lines.join('\n')}`;
}

export function buildLoopTooltip(al: AutoLoopSnapshot): string {
  const tokenLine = `${al.tokensUsed.toLocaleString()} / ${al.maxTokens.toLocaleString()} orchestrator tokens`;
  const startedAt = (() => { try { return new Date(al.startedAt).toLocaleString(); } catch { return al.startedAt; } })();
  const lastQ = (al.lastQuestion ?? '').slice(0, 160);
  const lastErr = al.lastError ? `\nLast error: ${al.lastError}` : '';
  return `Auto-loop: orchestrator answering NEEDS_INPUT for this task.\n` +
         `Iteration ${al.iteration} of ${al.maxIterations}.\n` +
         `${tokenLine}.\nStarted ${startedAt}.${lastErr}\n\nLast question: ${lastQ}${(al.lastQuestion ?? '').length > 160 ? '...' : ''}`;
}

export function buildPendingTooltip(pi: PendingIntent): string {
  const when = (() => {
    try { return new Date(pi.savedAt).toLocaleString(); }
    catch { return pi.savedAt; }
  })();
  const preview = (pi.prompt ?? '').slice(0, 120);
  return `Pending follow-up (${pi.mode}) saved ${when}.\nWill run on next auto-pickup.\n\n${preview}${(pi.prompt ?? '').length > 120 ? '...' : ''}`;
}

// Context-menu action ids shared between the menu builder and the click
// handler in the component.
export const EPIC_ASSIGN_PREFIX = 'epic-assign:';
export const EPIC_DETACH_ID = 'epic-detach';
export const FILTER_DEPENDENTS_ID = 'filter-dependents';
/**
 * Destructive "Delete task" context-menu row. Replaces the hover trash button
 * that used to sit on every card (fehlklick-risk right where you click/drag).
 * Clicking it drives the same `deleteRequested` flow — the parent still owns
 * the confirm/undo semantics.
 */
export const DELETE_ID = 'delete-task';

/**
 * Right-click context-menu rows for a card: copy actions + (for non-epic cards)
 * the epic assign/detach submenu. The active epic is marked; a detach row is
 * appended only when the card is already attached to one.
 */
export function buildCardCtxMenuItems(
  job: TaskInfo,
  isEpic: boolean,
  epics: readonly EpicRollup[],
  currentEpicId: string | null,
  mutationsBlocked = false,
): MenuItem[] {
  const items: MenuItem[] = [
    { kind: 'row', id: 'copy-name', label: 'Copy Name' },
    { kind: 'row', id: 'copy-id', label: 'Copy ID' },
  ];
  if (job.key) {
    items.push({ kind: 'row', id: 'copy-key', label: `Copy Key (${job.key})` });
    items.push({
      kind: 'row',
      id: FILTER_DEPENDENTS_ID,
      label: `Filter: tasks depending on ${job.key}`,
    });
  }

  // Epic assignment is only meaningful for ordinary task cards - an epic is not
  // a sub-task of another epic.
  if (!isEpic) {
    items.push({ kind: 'separator' });
    items.push({ kind: 'header', label: 'Epic' });
    if (epics.length === 0 && !currentEpicId) {
      items.push({ kind: 'row', id: 'epic-none', label: 'No epics in this project', disabled: true });
    } else {
      for (const epic of epics) {
        items.push({
          kind: 'row',
          id: EPIC_ASSIGN_PREFIX + epic.id,
          label: epic.title || epic.id,
          active: epic.id === currentEpicId,
          disabled: mutationsBlocked,
        });
      }
      if (currentEpicId) {
        items.push({
          kind: 'row',
          id: EPIC_DETACH_ID,
          label: 'Detach from epic',
          disabled: mutationsBlocked,
        });
      }
    }
  }

  // Destructive delete lives at the very end behind a separator so it never
  // sits next to the everyday copy/assign rows. Present on every card — for an
  // epic card it may be the only actionable row, which the operator accepted.
  items.push({ kind: 'separator' });
  items.push({
    kind: 'row',
    id: DELETE_ID,
    label: isEpic ? 'Delete epic' : 'Delete task',
    danger: true,
    disabled: mutationsBlocked,
  });
  return items;
}
