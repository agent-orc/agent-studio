import { TaskState } from '../models/task.model';
import type { TaskInfo, TaskRunActivity, TaskRunActivityKind } from '../models/task.model';
import type { StructuredTooltip } from 'coding-agent-chat/shared';

export type RunActivityTone = 'active' | 'failed' | 'idle';

export interface RunActivityBadge {
  kind: TaskRunActivityKind;
  label: string;
  tone: RunActivityTone;
  tooltip: StructuredTooltip;
}

/**
 * AGT-2703: resolves the run-activity kind a card should render from the kind
 * the backend sent plus the client's own clock.
 *
 * The server used to decide `failed-backoff` itself by comparing the recorded
 * backoff deadline against its clock. That made the whole board response a
 * function of the instant it was served, so no two responses were ever
 * comparable and the board could never answer a conditional poll. The backoff
 * deadline is in the payload anyway, so the comparison belongs here: the server
 * now sends the kind the card shows once the deadline has passed
 * (`failed-idle` or `no-active-run`) together with `backoffUntil`, and this
 * function reinstates `failed-backoff` for as long as the deadline is still
 * ahead. A live run never carries a deadline and is returned untouched.
 *
 * Callers that already track a clock signal pass it in, so a card holding out a
 * backoff flips to its post-backoff state on the next tick rather than waiting
 * for a server round-trip.
 */
export function resolveRunActivityKind(
  activity: TaskRunActivity | null | undefined,
  nowMs: number = Date.now(),
): TaskRunActivityKind | null {
  if (!activity) return null;
  if (activity.kind === 'active' || activity.kind === 'continuing-after-restart') return activity.kind;

  const until = activity.backoffUntil ? Date.parse(activity.backoffUntil) : Number.NaN;
  if (Number.isFinite(until) && until > nowMs) return 'failed-backoff';

  // A deadline that has passed, or a payload from a backend that still
  // classified the backoff itself, resolves to what the card shows once the
  // backoff is over, so a cooling card can never get stuck on the retry clock.
  return activity.kind === 'failed-backoff'
    ? (activity.attempt > 0 ? 'failed-idle' : 'no-active-run')
    : activity.kind;
}

/**
 * Grace period for a Progress card that has no live run and no explicit
 * failure. It matches the backend's execution-location freshness window, so a
 * newly claimed card or a run whose registry is briefly rebuilding is not
 * presented as stranded.
 */
export const STALLED_IDLE_THRESHOLD_MS = 3 * 60_000;

export interface StalledTaskState {
  reason: 'failed' | 'idle';
  label: 'Stalled';
  tooltip: string;
}

export interface ActiveTaskRun {
  kind: 'local' | 'remote';
  runnerId: string | null;
  hostDisplayName: string | null;
  startedAt: string | null;
  lastHeartbeat: string | null;
}

/**
 * Canonical active-run projection shared by board cards, liveness copy, and
 * aggregate counters. When the backend supplied an execution location it wins:
 * a disconnected or recovering remote lease must not be resurrected by the
 * compatibility runner badge. Older payloads can still prove a live run from
 * the active lease or local execution fields.
 */
export function deriveActiveTaskRun(job: TaskInfo): ActiveTaskRun | null {
  if (job.state !== TaskState.Progress) return null;

  const location = job.executionLocation;
  if (location) {
    const connected = location.connectionState === 'connected';
    if (connected && (location.state === 'remote-running' || location.state === 'local-running')) {
      return {
        kind: location.state === 'remote-running' ? 'remote' : 'local',
        runnerId: location.runnerId ?? null,
        hostDisplayName: location.hostDisplayName ?? location.runnerId ?? null,
        startedAt: location.startedAt ?? null,
        lastHeartbeat: location.lastHeartbeat ?? null,
      };
    }
    return null;
  }

  const runner = job.runner;
  if (runner?.leaseId) {
    return {
      kind: runner.isRemote ? 'remote' : 'local',
      runnerId: runner.runnerId || null,
      hostDisplayName: runner.runnerName || runner.hostname || runner.runnerId || null,
      startedAt: runner.acquiredAt || null,
      lastHeartbeat: null,
    };
  }

  if (job.execution?.status === 'running'
    || job.runActivity?.kind === 'active'
    || job.runActivity?.kind === 'continuing-after-restart') {
    return {
      kind: 'local',
      runnerId: null,
      hostDisplayName: 'Local',
      startedAt: job.execution?.startedAt ?? null,
      lastHeartbeat: null,
    };
  }

  return null;
}

function latestActivityMs(job: TaskInfo): number | null {
  const instants = [
    job.enteredLaneAt,
    job.phaseEnteredAt,
    job.executionLocation?.lastActivityAt,
    job.executionLocation?.lastHeartbeat,
    job.lastActivity,
    job.createdAt,
  ]
    .map((value) => value ? Date.parse(value) : Number.NaN)
    .filter(Number.isFinite);
  return instants.length > 0 ? Math.max(...instants) : null;
}

/**
 * Current-run liveness is intentionally derived from every positive runtime
 * projection. `runActivity` is built from the runner registry before the live
 * pipeline overlay is attached, so it can briefly say no-active-run while a
 * pre-step is already running. Between pipeline steps there may be no
 * `activeStep`, but execution ownership remains authoritative.
 */
export function isTaskRunActive(job: TaskInfo): boolean {
  if (job.liveStatus?.activeStep != null
    || job.execution?.status === 'running'
    || job.runActivity?.kind === 'active'
    || job.runActivity?.kind === 'continuing-after-restart') {
    return true;
  }
  const location = job.executionLocation;
  if (location) {
    return (location.state === 'local-running' || location.state === 'remote-running')
      && location.connectionState === 'connected';
  }
  return job.runner != null;
}

/**
 * Instants that mark a *state transition of the task itself*, used to decide
 * which of two representations of one task saw the world later.
 *
 * Deliberately narrower than `latestActivityMs`: the runtime heartbeat fields
 * (`executionLocation.lastHeartbeat` / `.lastActivityAt`) are run liveness, not
 * lane provenance. A run keeps heartbeating while it is still attached to the
 * lane it was picked up in, so its heartbeat can be newer than a lane move it
 * knows nothing about — comparing it would reintroduce exactly the regression
 * this ordering exists to prevent.
 */
function stateStampMs(job: TaskInfo): number | null {
  const instants = [
    job.enteredLaneAt,
    job.phaseEnteredAt,
    job.lastActivity,
    job.createdAt,
  ]
    .map((value) => value ? Date.parse(value) : Number.NaN)
    .filter(Number.isFinite);
  return instants.length > 0 ? Math.max(...instants) : null;
}

/**
 * AGT-2378 — pick the freshest `TaskInfo` for a run-liveness derivation.
 *
 * A task tab / side panel renders a `TaskDetail` that was fetched once when the
 * task was opened; nothing re-syncs that snapshot afterwards. The board list, in
 * contrast, is kept current by the jobs hub push plus its heartbeat poll, and it
 * carries the same runtime overlay (`runActivity`, `runner`, `execution`,
 * `executionLocation`, `liveStatus`). Deriving liveness from the frozen snapshot
 * therefore pins the detail on whatever the run looked like at open time — for a
 * remote run, where no local CLI output poll can paper over it, that shows up as
 * a permanent "kein aktiver Run" next to a card that is demonstrably running.
 *
 * The board entry is not unconditionally newer, though. A mutation performed in
 * the detail (a lane move, say) updates the snapshot immediately, while the
 * board push that carries the same change can be seconds behind — letting the
 * live entry win there would visibly jump the display back to the pre-mutation
 * lane. So the live entry only overrides the snapshot when it is demonstrably at
 * least as fresh:
 *   (a) same lane — the two can only disagree about run liveness, and there the
 *       live overlay is by construction the more current one;
 *   (b) different lane — the newer state stamp wins, and a tie or a missing
 *       stamp on either side keeps the snapshot (conservative: never step back).
 *
 * Falls back to the snapshot when the task is not in the live list (filtered
 * away, archived, or a cross-project detail opened from search).
 */
export function freshestRunInfo(snapshot: TaskInfo, liveJobs: readonly TaskInfo[]): TaskInfo {
  const live = snapshot.taskKey
    ? liveJobs.find(job => job.taskKey === snapshot.taskKey)
    : liveJobs.find(job => job.id === snapshot.id);
  if (!live) return snapshot;
  if (live.state === snapshot.state) return live;

  const liveMs = stateStampMs(live);
  const snapshotMs = stateStampMs(snapshot);
  if (liveMs === null || snapshotMs === null) return snapshot;
  return liveMs > snapshotMs ? live : snapshot;
}

/**
 * Pure board-level derivation of an acute stranded Progress task. A failed run
 * needs attention immediately once no process owns it. A task with no recorded
 * failure gets a short grace period before it is called stalled. Scheduled
 * rapid-crash backoff is deliberately excluded because it already has a known
 * recovery path and its own countdown banner.
 */
export function deriveStalledTaskState(
  job: TaskInfo,
  nowMs: number = Date.now(),
  idleThresholdMs: number = STALLED_IDLE_THRESHOLD_MS,
): StalledTaskState | null {
  if (job.state !== TaskState.Progress || isTaskRunActive(job)) return null;
  const kind = resolveRunActivityKind(job.runActivity, nowMs);
  if (kind === 'failed-backoff') return null;

  const failed = kind === 'failed-idle'
    || job.execution?.status === 'failed'
    || ((job.outcomeIssue?.severity ?? '').toLowerCase() === 'warn')
    || ((job.outcomeIssue?.severity ?? '').toLowerCase() === 'high');
  if (failed) {
    const detail = job.runActivity?.lastError || job.outcomeIssue?.summary || 'The last run ended with an error.';
    return {
      reason: 'failed',
      label: 'Stalled',
      tooltip: `Needs attention: no run is active and the last run failed. ${detail}`,
    };
  }

  const latest = latestActivityMs(job);
  if (latest === null || nowMs - latest <= idleThresholdMs) return null;
  const idleMinutes = Math.max(1, Math.floor((nowMs - latest) / 60_000));
  return {
    reason: 'idle',
    label: 'Stalled',
    tooltip: `Needs attention: this task is still In Progress, but no run is active and no activity has arrived for ${idleMinutes} minutes.`,
  };
}

function escapeHtml(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

function formatClock(iso: string): string | null {
  const ms = Date.parse(iso);
  if (Number.isNaN(ms)) return null;
  return new Date(ms).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
}

/**
 * ASS-1751: descriptor for the small, quiet run-activity pill shown on a
 * `3-progress` card and in the task-detail header. It disambiguates the three
 * ways a progress card can otherwise look "untouched": a live run occupying a
 * slot, a failed run waiting out the rapid-crash backoff (with the retry time),
 * and an orphan whose run was killed by a backend restart. Returns null off the
 * Progress lane or when the backend attached no `runActivity`. Pure visibility —
 * carries no action. Shared by the board card and the detail header so both
 * read identical copy from one source. The clock is injected (`nowMs`) so
 * callers can drive it from a shared tick signal.
 */
export function buildRunActivityBadge(job: TaskInfo, nowMs: number = Date.now()): RunActivityBadge | null {
  // Lane is authoritative: this is a 3-progress affordance only. The backend
  // overlay only attaches runActivity for Progress, but guard against a stale
  // poll snapshot the same way the execution badge does.
  if (job.state !== TaskState.Progress) return null;
  const activity = job.runActivity;
  if (!activity) return null;

  const attemptLine = activity.attempt > 0
    ? `<div><b>Versuch:</b> ${activity.attempt}</div>`
    : '';
  const errorLine = activity.lastError
    ? `<div><b>Letzter Fehler:</b> ${escapeHtml(activity.lastError)}</div>`
    : '';

  // Positive live evidence wins over a stale negative runner classification.
  // This is most visible during pre-steps (activeStep, no CLI execution yet)
  // and in the hand-off between pipeline steps.
  const resolved = resolveRunActivityKind(activity, nowMs) ?? activity.kind;
  const effectiveKind: TaskRunActivityKind = resolved === 'continuing-after-restart'
    ? resolved
    : isTaskRunActive(job) ? 'active' : resolved;

  switch (effectiveKind) {
    case 'active': {
      const projectedPid = activity.processId
        ?? job.execution?.processId
        ?? job.executionLocation?.processId;
      const pid = typeof projectedPid === 'number' && projectedPid > 0 ? projectedPid : null;
      return {
        kind: 'active',
        label: 'Run aktiv',
        tone: 'active',
        tooltip: {
          title: 'Run aktiv (PID lebt)',
          body: `<div>Ein Run-Prozess läuft und belegt einen Slot.</div>${pid !== null ? `<div><b>PID:</b> ${pid}</div>` : ''}${attemptLine}${errorLine}`,
        },
      };
    }
    case 'continuing-after-restart': {
      const projectedPid = activity.processId
        ?? job.execution?.processId
        ?? job.executionLocation?.processId;
      const pid = typeof projectedPid === 'number' && projectedPid > 0 ? projectedPid : null;
      return {
        kind: activity.kind,
        label: 'Continuing after restart',
        tone: 'active',
        tooltip: {
          title: 'Continuing after restart',
          body: `<div>The replacement backend verified and reattached the same durable worker.</div>${pid !== null ? `<div><b>PID:</b> ${pid}</div>` : ''}${attemptLine}${errorLine}`,
        },
      };
    }
    case 'failed-backoff': {
      // Reached only while the deadline is still ahead, so the clock is the
      // headline rather than a conditional decoration.
      const clock = activity.backoffUntil ? formatClock(activity.backoffUntil) : null;
      const label = clock ? `failed · Backoff bis ${clock}` : 'failed · wartet auf Reissue';
      return {
        kind: effectiveKind,
        label,
        tone: 'failed',
        tooltip: {
          title: 'failed — wartet auf Reissue/Review',
          body: `<div>Der letzte Run ist fehlgeschlagen; ein Rapid-Crash-Backoff hält das Re-Pickup${clock ? ` bis <b>${clock}</b>` : ''} zurück.</div>${attemptLine}${errorLine}`,
        },
      };
    }
    case 'failed-idle': {
      return {
        kind: effectiveKind,
        label: 'failed · kein aktiver Run',
        tone: 'failed',
        tooltip: {
          title: 'failed — kein aktiver Run',
          body: `<div>Der letzte Run ist fehlgeschlagen und aktuell läuft nichts; der Task ist wieder aufnahmebereit.</div>${attemptLine}${errorLine}`,
        },
      };
    }
    case 'no-active-run':
    default: {
      return {
        kind: 'no-active-run',
        label: 'kein aktiver Run',
        tone: 'idle',
        tooltip: {
          title: 'kein aktiver Run',
          body: `<div>Kein Run bearbeitet diesen Task gerade. Er liegt in 3-progress und wartet auf Aufnahme — z. B. wurde ein früherer Run durch einen Backend-Neustart beendet.</div>${attemptLine}${errorLine}`,
        },
      };
    }
  }
}
