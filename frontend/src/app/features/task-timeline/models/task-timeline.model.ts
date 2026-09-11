/**
 * Frontend mirror of the backend per-task event ledger (`logs/timeline.jsonl`,
 * ADR-0049 / ASS-566). One row per event in a task's lifetime: prompt
 * creation, agent runs, pipeline steps, and the orchestrator's
 * completion-loop verdicts (accept / reopen / escalate). Served by
 * `GET /api/tasks/{id}/timeline` and rendered by the Timeline tab + the
 * Overview attempt-cycle indicator.
 *
 * The wire shape is camelCase (the backend serializes with a camelCase
 * naming policy); keep this interface in sync with
 * `src/AgentTaskboard.Shared/Models/TimelineEvent.cs`.
 */
import { resolveAspectFindings, type AspectFinding } from '../../../components/aspect-findings';

export interface TaskTimelineEvent {
  ts: string;
  kind: string;
  /** 'agent' | 'orchestrator' | 'quality-loop' | 'system' | 'human:<email>'. */
  actor: string;
  runId?: string | null;
  payloadRef?: string | null;
  summary: string;
  details?: Record<string, string> | null;
}

/**
 * The closed set of timeline event kinds. Mirrors
 * `TimelineEventKinds` on the backend. Used so the FE matches on the
 * stable wire string rather than scattering literals.
 */
export const TIMELINE_KIND = {
  promptCreated: 'prompt_created',
  agentRunStarted: 'agent_run_started',
  quotaFallbackActivated: 'quota_fallback_activated',
  quotaAdmissionDecision: 'quota_admission_decision',
  loadThrottleDecision: 'load_throttle_decision',
  runnerSlotAdmission: 'runner_slot_admission',
  integrationLease: 'integration_lease',
  agentRunFinished: 'agent_run_finished',
  preStepStarted: 'pre_step_started',
  preStepFinished: 'pre_step_finished',
  postStepStarted: 'post_step_started',
  postStepFinished: 'post_step_finished',
  orchestratorEscalated: 'orchestrator_escalated',
  orchestratorSteered: 'orchestrator_steered',
  steerTimeoutResolved: 'steer_timeout_resolved',
  orchestratorVerdictAccepted: 'orchestrator_verdict_accepted',
  qualityLoopReopened: 'quality_loop_reopened',
  humanReviewDecided: 'human_review_decided',
  operatorRequeued: 'operator_requeued',
  postAcceptanceReviewReportRecorded: 'post_acceptance_review_report_recorded',
  taskReleased: 'task_released',
  laneChanged: 'lane_changed',
  epicDecomposed: 'epic_decomposed',
  mergedIn: 'merged_in',
  readOnlyContainmentViolation: 'read_only_containment_violation',
  executionContext: 'execution_context',
  taskSpawned: 'task_spawned',
  externalCompletion: 'external_completion',
  deliveryUnverified: 'delivery_unverified',
  integrationPendingWarning: 'integration_pending_warning',
  integrationRecoveryQueued: 'integration_recovery_queued',
} as const;

/** The three terminals of the completion loop. */
export type CompletionLoopVerdict = 'accepted' | 'reopened' | 'escalated';

/**
 * Derived "where is the completion loop right now" summary used by the
 * Overview attempt-cycle indicator. Computed purely from the timeline
 * events so the FE never has to re-derive the loop from the decision
 * journal or chat log.
 */
export interface CompletionLoopState {
  /** The most recent loop terminal, or null when the loop has not produced one yet. */
  latestVerdict: CompletionLoopVerdict | null;
  /** Number of times the orchestrator re-opened the task ("go again"). */
  reopenCount: number;
  /** Current attempt number when known (from the latest reopen/escalate event). */
  attempt: number | null;
  /** Configured attempt budget when known. */
  maxAttempts: number | null;
  /** One-line gap / reason for the latest verdict. */
  reason: string | null;
  /**
   * Structured per-aspect findings behind the latest verdict, when it was
   * aspect-driven. Resolved from the event's structured `findings` JSON or
   * by parsing the legacy `**{aspect}** [{verdict}]: {reason}` blob, so the
   * Overview strip can render toned chips instead of the raw `reason` text.
   * Empty for non-aspect verdicts.
   */
  findings: AspectFinding[];
  /** Timestamp of the latest verdict event. */
  at: string | null;
  /** True once the loop has produced at least one verdict event. */
  hasActivity: boolean;
}

const VERDICT_KINDS = new Set<string>([
  TIMELINE_KIND.orchestratorVerdictAccepted,
  TIMELINE_KIND.qualityLoopReopened,
  TIMELINE_KIND.orchestratorEscalated,
]);

/**
 * Lanes a card sits in before a run starts. An operator moving a card from a
 * loop terminal back into one of these opens a fresh attempt: the previous
 * verdict is history, not the current state of the loop.
 */
const PRE_RUN_LANES: ReadonlySet<string> = new Set(['0-backlog', '1-preparation', '2-ready']);

/** Lanes the completion loop leaves a card in (its terminals plus the archive). */
const LOOP_TERMINAL_LANES: ReadonlySet<string> = new Set([
  '5e-escalated',
  '5-human-review',
  '6-completed',
  '7-archive',
]);

/**
 * True when the event reopens the task for a fresh attempt by operator
 * action: an explicit requeue, or a lane change from a loop terminal back
 * into a pre-run lane. A quality-loop reopen (auto-review to ready) is not
 * such an event; it is part of the loop and keeps its `reopened` verdict.
 */
function reopensForFreshAttempt(event: TaskTimelineEvent): boolean {
  if (event.kind === TIMELINE_KIND.operatorRequeued) return true;
  if (event.kind !== TIMELINE_KIND.laneChanged) return false;
  const from = event.details?.['from'] ?? '';
  const to = event.details?.['to'] ?? '';
  return LOOP_TERMINAL_LANES.has(from) && PRE_RUN_LANES.has(to);
}

function verdictOf(kind: string): CompletionLoopVerdict | null {
  switch (kind) {
    case TIMELINE_KIND.orchestratorVerdictAccepted: return 'accepted';
    case TIMELINE_KIND.qualityLoopReopened: return 'reopened';
    case TIMELINE_KIND.orchestratorEscalated: return 'escalated';
    default: return null;
  }
}

function parseIntOrNull(value: string | undefined | null): number | null {
  if (value == null) return null;
  const n = Number.parseInt(value, 10);
  return Number.isFinite(n) ? n : null;
}

/**
 * Project the raw timeline events into the completion-loop summary the
 * Overview indicator renders. Events arrive in chronological (append)
 * order, so the last verdict event in the list is the current state of
 * the loop.
 *
 * - `reopenCount` is the number of `quality_loop_reopened` events.
 * - `attempt` / `maxAttempts` come from the latest reopen/escalate
 *   event's `details`; an `accepted` terminal carries no attempt counter,
 *   so we fall back to `reopenCount + 1` (initial run + each reopen).
 * - `reason` prefers the structured `gap` (reopen) / `reason` (escalate)
 *   detail and falls back to the event summary.
 * - An operator requeue, or a lane change from a loop terminal back into a
 *   pre-run lane, closes the loop that produced the latest verdict. Until a
 *   new verdict arrives the state is empty again, so a card waiting in Ready
 *   does not carry an "Escalated to human" flag from a run weeks ago
 *   (AGT-2373, 2026-09-06). The Timeline tab still lists the old verdict.
 */
export function deriveCompletionLoop(events: readonly TaskTimelineEvent[]): CompletionLoopState {
  const empty: CompletionLoopState = {
    latestVerdict: null,
    reopenCount: 0,
    attempt: null,
    maxAttempts: null,
    reason: null,
    findings: [],
    at: null,
    hasActivity: false,
  };
  if (!events || events.length === 0) return empty;

  let reopenCount = 0;
  let latest: TaskTimelineEvent | null = null;
  for (const e of events) {
    if (reopensForFreshAttempt(e)) {
      reopenCount = 0;
      latest = null;
      continue;
    }
    if (!VERDICT_KINDS.has(e.kind)) continue;
    if (e.kind === TIMELINE_KIND.qualityLoopReopened) reopenCount++;
    latest = e;
  }
  if (latest == null) return empty;

  const latestVerdict = verdictOf(latest.kind);
  const details = latest.details ?? {};
  const attempt =
    parseIntOrNull(details['attempt']) ??
    (latestVerdict === 'accepted' ? reopenCount + 1 : null);
  const reason =
    (details['gap'] ?? details['reason'] ?? latest.summary ?? '').trim() || null;
  const findings = resolveAspectFindings(details['findings'], details['gap'] ?? details['reason']);

  return {
    latestVerdict,
    reopenCount,
    attempt,
    maxAttempts: parseIntOrNull(details['maxAttempts']),
    reason,
    findings,
    at: latest.ts ?? null,
    hasActivity: true,
  };
}

/** Tone suffix used to colour verdict surfaces consistently across the
 *  Overview indicator and the Timeline banner. */
export type VerdictTone = 'ok' | 'warn' | 'danger' | 'neutral';

/** Human label for a completion-loop verdict. */
export function verdictLabel(v: CompletionLoopVerdict | null): string {
  switch (v) {
    case 'accepted':  return 'Accepted';
    case 'reopened':  return 'Re-opened';
    case 'escalated': return 'Escalated to human';
    default:          return 'In progress';
  }
}

/** Text-only glyph for a verdict (no emoji in structural surfaces). */
export function verdictGlyph(v: CompletionLoopVerdict | null): string {
  switch (v) {
    case 'accepted':  return '✓';
    case 'reopened':  return '↻';
    case 'escalated': return '⚑';
    default:          return '·';
  }
}

/** Tone class suffix so a verdict pill / banner colours by outcome. */
export function verdictTone(v: CompletionLoopVerdict | null): VerdictTone {
  switch (v) {
    case 'accepted':  return 'ok';
    case 'reopened':  return 'warn';
    case 'escalated': return 'danger';
    default:          return 'neutral';
  }
}
