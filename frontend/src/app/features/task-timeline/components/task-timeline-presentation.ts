import type { CliContextSource } from '../../run-timeline';
import { TIMELINE_KIND, type TaskTimelineEvent } from '../models/task-timeline.model';

export interface TimelineDetailEntry {
  key: string;
  label: string;
  value: string;
}

export interface TimelineSourceDisclosure {
  label: string;
  origin: string | null;
  sources: CliContextSource[];
}

const KIND_LABELS: Readonly<Record<string, string>> = {
  [TIMELINE_KIND.promptCreated]: 'Prompt created',
  [TIMELINE_KIND.agentRunStarted]: 'Run started',
  [TIMELINE_KIND.quotaFallbackActivated]: 'Quota fallback activated',
  [TIMELINE_KIND.quotaAdmissionDecision]: 'Quota admission decision',
  [TIMELINE_KIND.loadThrottleDecision]: 'Run deferred for host load',
  [TIMELINE_KIND.runnerSlotAdmission]: 'Slot admitted',
  [TIMELINE_KIND.runContinuedAfterRestart]: 'Continuing after restart',
  [TIMELINE_KIND.runLostAcrossRestart]: 'Run lost across restart',
  [TIMELINE_KIND.integrationLease]: 'Integration lease',
  [TIMELINE_KIND.agentRunFinished]: 'Run finished',
  [TIMELINE_KIND.preStepStarted]: 'Pre-step started',
  [TIMELINE_KIND.preStepFinished]: 'Pre-step finished',
  [TIMELINE_KIND.postStepStarted]: 'Post-step started',
  [TIMELINE_KIND.postStepFinished]: 'Post-step finished',
  [TIMELINE_KIND.orchestratorEscalated]: 'Escalated to human',
  [TIMELINE_KIND.orchestratorSteered]: 'Steered',
  [TIMELINE_KIND.steerTimeoutResolved]: 'Steer timeout resolved',
  [TIMELINE_KIND.orchestratorVerdictAccepted]: 'Verdict accepted',
  [TIMELINE_KIND.qualityLoopReopened]: 'Re-opened',
  [TIMELINE_KIND.humanReviewDecided]: 'Human review decided',
  [TIMELINE_KIND.operatorRequeued]: 'Requeued by operator',
  [TIMELINE_KIND.postAcceptanceReviewReportRecorded]: 'Post-acceptance review recorded',
  [TIMELINE_KIND.taskReleased]: 'Released for dependents',
  [TIMELINE_KIND.laneChanged]: 'Lane changed',
  [TIMELINE_KIND.epicDecomposed]: 'Epic decomposed',
  [TIMELINE_KIND.mergedIn]: 'Merged in',
  [TIMELINE_KIND.readOnlyContainmentViolation]: 'Containment violation',
  [TIMELINE_KIND.executionContext]: 'Execution context',
  [TIMELINE_KIND.taskSpawned]: 'Follow-up task spawned',
  [TIMELINE_KIND.externalCompletion]: 'Completed externally',
  [TIMELINE_KIND.deliveryUnverified]: 'Delivery unverified - stamp refused',
  [TIMELINE_KIND.integrationPendingWarning]: 'Delivery not integrated',
  [TIMELINE_KIND.integrationRecoveryQueued]: 'Integration recovery queued',
};

const HIDDEN_DETAILS = new Set([
  'gap', 'reason', 'findings', 'attempt', 'maxAttempts', 'followUpPrompt',
]);

const HIDDEN_BY_KIND: Readonly<Record<string, ReadonlySet<string>>> = {
  [TIMELINE_KIND.agentRunFinished]: new Set(['cli', 'status', 'durationSeconds']),
  [TIMELINE_KIND.runnerSlotAdmission]: new Set(['slot', 'maxParallelism']),
  [TIMELINE_KIND.executionContext]: new Set([
    'cli', 'source', 'sources', 'sourceItems', 'mcp', 'model', 'thinkingLevel', 'permissionMode',
  ]),
  [TIMELINE_KIND.laneChanged]: new Set(['from', 'to']),
  // `released` is the title; the dependent key list is the audit value and stays.
  [TIMELINE_KIND.taskReleased]: new Set(['released']),
  [TIMELINE_KIND.taskSpawned]: new Set(['targetProject', 'targetKey', 'targetJobId']),
  [TIMELINE_KIND.externalCompletion]: new Set(['source']),
};

const DEFAULT_VALUES = new Set(['', '0', 'false', 'none', 'null', 'unknown', 'yolo']);
const COUNT_KEY = /(?:count|sources|mcp|artifacts|commits|items|files|created)$/i;

export function timelineKindLabel(kind: string): string {
  return KIND_LABELS[kind] ?? humanize(kind);
}

export function timelineEventTitle(event: TaskTimelineEvent): string {
  if (event.kind === TIMELINE_KIND.agentRunStarted) {
    return 'Run started';
  }

  if (event.kind === TIMELINE_KIND.agentRunFinished) {
    const status = clean(event.details?.['status']) ?? statusFromRunSummary(event.summary);
    const outcome = status === 'completed'
      ? 'finished'
      : status === 'failed'
        ? 'failed'
        : status === 'stopped' || status === 'cancelled'
          ? 'stopped'
          : 'ended';
    const duration = runDuration(event);
    return `Run ${outcome}${duration ? ` · ${duration}` : ''}`;
  }

  if (isPipelineStep(event.kind)) {
    const phase = event.kind.startsWith('pre_') ? 'Pre-step' : 'Post-step';
    const recordedReason = clean(event.details?.['reason'])?.toLowerCase();
    const rawStatus = clean(event.details?.['status'])?.toLowerCase();
    const recordedStatus = rawStatus === 'skipped' && recordedReason === 'no verify commands derivable'
      ? 'notapplicable'
      : rawStatus;
    const status = event.kind.endsWith('_started')
      ? 'started'
      : recordedStatus === 'passed'
        ? 'passed'
        : recordedStatus === 'failed'
          ? 'failed'
          : recordedStatus === 'skipped'
            ? 'skipped'
            : recordedStatus === 'notapplicable' || recordedStatus === 'not-applicable'
              ? 'not applicable'
          : 'finished';
    const subject = stepSummarySubject(event.summary);
    return `${phase} ${status}${subject ? ` · ${subject}` : ''}`;
  }

  if (event.kind === TIMELINE_KIND.runnerSlotAdmission) {
    const slot = clean(event.details?.['slot']);
    const maximum = clean(event.details?.['maxParallelism']);
    return `Slot admitted${slot && maximum ? ` · ${slot}/${maximum}` : ''}`;
  }

  if (event.kind === TIMELINE_KIND.humanReviewDecided) {
    const decision = clean(event.details?.['decision'])?.toLowerCase();
    if (decision === 'accept') return 'Human review accepted';
    if (decision === 'reject') return 'Human review rejected';
    if (decision === 'reissue') return 'Human review requested changes';
  }

  if (event.kind === TIMELINE_KIND.operatorRequeued) {
    const reason = clean(event.details?.['reason']);
    return `Requeued by operator${reason ? ` · ${reason}` : ''}`;
  }

  if (event.kind === TIMELINE_KIND.laneChanged && clean(event.summary)) {
    return `Lane changed · ${event.summary.trim()}`;
  }

  // AGT-2709: the release flag is reversible, so the row has to name the
  // direction rather than reuse the one kind label for both decisions.
  if (event.kind === TIMELINE_KIND.taskReleased) {
    return clean(event.details?.['released']) === 'false'
      ? 'Release withdrawn'
      : 'Released for dependents';
  }

  if (event.kind === TIMELINE_KIND.epicDecomposed) {
    const created = clean(event.details?.['created']);
    return `Epic decomposed${created ? ` · ${created} task${created === '1' ? '' : 's'}` : ''}`;
  }

  if (event.kind === TIMELINE_KIND.taskSpawned) {
    const key = clean(event.details?.['targetKey']);
    const project = clean(event.details?.['targetProject']);
    if (key && project) return `Follow-up spawned · ${key} in ${project}`;
    if (key) return `Follow-up spawned · ${key}`;
  }

  if (event.kind === TIMELINE_KIND.externalCompletion) {
    const source = clean(event.details?.['source']);
    if (source) return `Completed externally · ${source}`;
  }

  if (event.kind === TIMELINE_KIND.integrationPendingWarning) {
    const branch = clean(event.details?.['integrationBranch']);
    return `Delivery pending${branch ? ` · ${branch}` : ''}`;
  }

  return timelineKindLabel(event.kind);
}

export function timelineEventSummary(event: TaskTimelineEvent): string | null {
  const summary = clean(event.summary);
  if (!summary) return null;
  if (event.kind === TIMELINE_KIND.executionContext
    || event.kind === TIMELINE_KIND.laneChanged
    || event.kind === TIMELINE_KIND.taskSpawned
    || event.kind === TIMELINE_KIND.taskReleased
    || event.kind === TIMELINE_KIND.externalCompletion
    || event.kind === TIMELINE_KIND.postAcceptanceReviewReportRecorded) {
    return null;
  }
  if (event.kind === TIMELINE_KIND.agentRunStarted
    && /^(?:\S+\s+)?(?:CLI\s+)?(?:run\s+)?start(?:ed)?$/i.test(summary)) {
    return null;
  }
  if (event.kind === TIMELINE_KIND.agentRunFinished
    && /^(?:\S+\s+)?run\s+(?:completed|failed|stopped|cancelled|unknown)(?:\s+after\s+[\d.,]+\s*s)?$/i.test(summary)) {
    return null;
  }
  if (isPipelineStep(event.kind) && stepSummarySubject(summary)) return null;
  if (event.kind === TIMELINE_KIND.humanReviewDecided
    && /^human review (?:accepted|rejected|requested changes)\b/i.test(summary)) {
    return null;
  }
  if (event.kind === TIMELINE_KIND.operatorRequeued
    && /^operator (?:reopened|requeued)\b/i.test(summary)) {
    return null;
  }
  if (event.kind === TIMELINE_KIND.epicDecomposed
    && /^epic decomposed\b/i.test(summary)) {
    return null;
  }

  const withoutRepeatedLead = compactSummary(event.kind, summary);
  if (!withoutRepeatedLead) return null;

  const normalizedSummary = normalize(withoutRepeatedLead);
  const normalizedTitle = normalize(timelineEventTitle(event));
  const normalizedKind = normalize(timelineKindLabel(event.kind));
  if (normalizedSummary === normalizedTitle || normalizedSummary === normalizedKind) return null;
  return withoutRepeatedLead;
}

export function timelineEventReason(event: TaskTimelineEvent): string | null {
  const reason = clean(event.details?.['reason']);
  if (!reason || DEFAULT_VALUES.has(reason.toLowerCase())) return null;
  const story = normalize(`${timelineEventTitle(event)} ${timelineEventSummary(event) ?? ''}`);
  return story.includes(normalize(reason)) ? null : reason;
}

export function timelineDetailEntries(event: TaskTimelineEvent): TimelineDetailEntry[] {
  const details = event.details;
  if (!details) return [];
  const hiddenForKind = HIDDEN_BY_KIND[event.kind] ?? new Set<string>();
  const story = normalize(`${timelineEventTitle(event)} ${timelineEventSummary(event) ?? ''}`);

  return Object.entries(details)
    .filter(([key, value]) => {
      if (HIDDEN_DETAILS.has(key) || hiddenForKind.has(key)) return false;
      const cleaned = clean(value);
      if (!cleaned || DEFAULT_VALUES.has(cleaned.toLowerCase())) return false;
      if (cliIsImpliedByModel(key, details)) return false;
      if (COUNT_KEY.test(key) && /^\d+$/.test(cleaned)) return false;
      return normalize(cleaned).length < 3 || !story.includes(normalize(cleaned));
    })
    .map(([key, value]) => ({ key, label: humanize(key), value: value.trim() }));
}

export function executionContextDisclosure(
  event: TaskTimelineEvent,
  fallbackSources: readonly CliContextSource[] = [],
): TimelineSourceDisclosure | null {
  if (event.kind !== TIMELINE_KIND.executionContext) return null;
  const sources = parseSources(event.details?.['sourceItems']);
  const resolved = sources.length > 0 ? sources : [...fallbackSources];
  const count = resolved.length;
  if (count === 0) return null;

  const source = clean(event.details?.['source']);
  const cli = clean(event.details?.['cli']);
  const origin = source === 'init-frame'
    ? 'CLI init frame'
    : source === 'convention'
      ? `${titleCase(cli ?? 'CLI')} config conventions`
      : source;
  return {
    label: `${count} source${count === 1 ? '' : 's'}`,
    origin,
    sources: resolved,
  };
}

function runDuration(event: TaskTimelineEvent): string | null {
  const raw = clean(event.details?.['durationSeconds'])
    ?? event.summary.match(/\bafter\s+([\d.,]+)\s*s\b/i)?.[1]
    ?? null;
  if (!raw) return null;
  const seconds = Number.parseFloat(raw.replace(',', '.'));
  if (!Number.isFinite(seconds) || seconds < 0) return null;
  return `${seconds.toLocaleString(undefined, { maximumFractionDigits: 1 })}s`;
}

function statusFromRunSummary(summary: string): string | null {
  return summary.match(/\brun\s+(completed|failed|stopped|cancelled|unknown)\b/i)?.[1]?.toLowerCase() ?? null;
}

function isPipelineStep(kind: string): boolean {
  return kind === TIMELINE_KIND.preStepStarted
    || kind === TIMELINE_KIND.preStepFinished
    || kind === TIMELINE_KIND.postStepStarted
    || kind === TIMELINE_KIND.postStepFinished;
}

function stepSummarySubject(summary: string): string | null {
  const match = clean(summary)?.match(/^(.+?)\s+(?:started|completed|finished|passed|failed|skipped|not applicable)\.?$/i);
  return match?.[1] ? sentenceCase(match[1]) : null;
}

function compactSummary(kind: string, summary: string): string {
  const patterns: Readonly<Partial<Record<string, RegExp>>> = {
    [TIMELINE_KIND.loadThrottleDecision]: /^launch deferred while\s+/i,
    [TIMELINE_KIND.runnerSlotAdmission]: /^admitted to slot \d+\s*\/\s*\d+\s*:\s*/i,
    [TIMELINE_KIND.runContinuedAfterRestart]: /^continuing after restart\s*:\s*/i,
    [TIMELINE_KIND.runLostAcrossRestart]: /^run lost across restart\s*:\s*/i,
    [TIMELINE_KIND.integrationLease]: /^integration lease\s+/i,
    [TIMELINE_KIND.orchestratorSteered]: /^steered\s+/i,
    [TIMELINE_KIND.steerTimeoutResolved]: /^steer timeout\s+/i,
    [TIMELINE_KIND.qualityLoopReopened]: /^re-opened\s*:\s*/i,
    [TIMELINE_KIND.orchestratorEscalated]: /^escalated(?: to human)?\s+/i,
    [TIMELINE_KIND.integrationRecoveryQueued]: /^integration recovery queued\s*:\s*/i,
  };
  if (kind === TIMELINE_KIND.integrationPendingWarning) {
    const detail = summary.match(/:\s*(.+)$/)?.[1];
    return detail ? sentenceCase(detail) : '';
  }
  const pattern = patterns[kind];
  return pattern ? sentenceCase(summary.replace(pattern, '')) : summary;
}

function parseSources(raw: string | undefined): CliContextSource[] {
  if (!raw) return [];
  try {
    const value: unknown = JSON.parse(raw);
    if (!Array.isArray(value)) return [];
    return value
      .filter((item): item is Record<string, unknown> => !!item && typeof item === 'object')
      .map(item => ({
        kind: typeof item['kind'] === 'string' ? item['kind'] : '',
        label: typeof item['label'] === 'string' ? item['label'] : 'Context source',
        path: typeof item['path'] === 'string' ? item['path'] : null,
        exists: typeof item['exists'] === 'boolean' ? item['exists'] : null,
        detail: typeof item['detail'] === 'string' ? item['detail'] : null,
      }));
  } catch {
    return [];
  }
}

function clean(value: string | null | undefined): string | null {
  const result = value?.trim();
  return result ? result : null;
}

function normalize(value: string): string {
  return value.toLowerCase().replace(/[^a-z0-9]+/g, ' ').trim();
}

function cliIsImpliedByModel(key: string, details: Readonly<Record<string, string>>): boolean {
  if (key === 'cli') return clean(details['model']) != null;
  if (!key.endsWith('Cli')) return false;
  return clean(details[`${key.slice(0, -3)}Model`]) != null;
}

function humanize(value: string): string {
  const spaced = value
    .replace(/[_-]+/g, ' ')
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .trim();
  return spaced ? spaced[0].toUpperCase() + spaced.slice(1) : value;
}

function titleCase(value: string): string {
  return value.length > 0 ? value[0].toUpperCase() + value.slice(1) : value;
}

function sentenceCase(value: string): string {
  const cleaned = value.trim();
  return cleaned.length > 0 ? cleaned[0].toUpperCase() + cleaned.slice(1) : '';
}
