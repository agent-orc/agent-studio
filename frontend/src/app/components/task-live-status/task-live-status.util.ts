import type { TaskInfo } from '../../models/task.model';
import { TaskState } from '../../models/task.model';

const FUTURE_SKEW_TOLERANCE_MS = 60 * 1000;

/**
 * The backend waitsOn overlay is the same archive-inclusive dependency truth
 * used by claim admission. A dependency gate therefore outranks any stale or
 * concurrent runner queue projection when choosing the one CURRENT wait state.
 *
 * AGT-2818: an unsatisfiable gate gets its own sentence. "Waits for release" and
 * "this gate can never open" describe different situations and only one of them
 * ends by waiting; a card that conflates them reads as queued for a month.
 */
export function dependencyWaitHeadline(task: TaskInfo): string | null {
  const waitsOn = task.waitsOn;
  if (!waitsOn?.blocked) return null;

  const open = waitsOn.items.filter(item => !item.fulfilled);
  const primary = open[0] ?? waitsOn.items[0];
  if (!primary) return waitsOn.cycleDetected ? 'Dependency gate blocked · cycle' : 'Dependency gate blocked';

  if (waitsOn.cycleDetected) return `Dependency gate blocked · cycle: ${primary.key}`;

  const blocked = open.find(item => item.unsatisfiable);
  if (blocked) return `this gate can never open: ${blocked.key}`;

  // Slim board cards carry only the first open items; openCount is the total.
  const extra = Math.max(0, (waitsOn.openCount ?? open.length) - 1);
  const reason = primary.waitingForRelease ? 'release' : 'completion';
  return `waits for ${reason}: ${primary.key}${extra > 0 ? ` +${extra}` : ''}`;
}

/** Short name for the non-dependency holds; the hold block states the detail. */
export function holdHeadline(mechanism: string): string {
  switch (mechanism) {
    case 'dispatch-rejection': return 'Queued, but the runner refused it';
    case 'crash-backoff': return 'Crash cooldown before the next attempt';
    default: return 'Queued, but not pickable';
  }
}

/**
 * AGT-3589: an idle card says what it waits for. "Between steps" named no
 * wait at all and read as work in flight on a Post Processing card nothing
 * was reviewing.
 */
export function idleHeadline(state: string): string {
  switch (state) {
    case TaskState.Preparation: return 'Preparing';
    case TaskState.Ready: return 'Waiting for a runner';
    case TaskState.AutoReview: return 'Waiting for review';
    default: return 'Waiting for the next step';
  }
}

export function timestamp(value: string | null | undefined): number | null {
  if (!value) return null;
  const parsed = Date.parse(value);
  return Number.isNaN(parsed) ? null : parsed;
}

export function clock(epochMs: number): string {
  return new Date(epochMs).toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit' });
}

export function latestActivityAt(task: TaskInfo, projected: string | null | undefined): number | null {
  const values = [
    projected,
    task.executionLocation?.lastActivityAt,
    // Board cards carry the heartbeat, not the derived activity stamp.
    task.executionLocation?.lastHeartbeat,
    task.lastActivity,
  ].map(timestamp).filter((value): value is number => value !== null);
  return values.length === 0 ? null : Math.max(...values);
}

/**
 * AGT-3589: a stamp ahead of this browser's clock (server clock skew, a
 * pinned demo clock) used to clamp to "0s ago", which claims activity that
 * did not happen. Past the tolerance the real stamp is shown instead.
 */
export function activityDetail(task: TaskInfo, projected: string | null | undefined, now: number): string | null {
  const latest = latestActivityAt(task, projected);
  if (latest === null) return null;
  if (latest - now > FUTURE_SKEW_TOLERANCE_MS) return `Last activity ${dateTime(latest)}`;
  return `Last activity ${elapsed(Math.max(0, now - latest))} ago`;
}

export function dateTime(epochMs: number): string {
  return new Date(epochMs).toLocaleString('en-US', {
    day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit',
  });
}

export function elapsed(milliseconds: number): string {
  const totalSeconds = Math.max(0, Math.floor(milliseconds / 1000));
  if (totalSeconds < 60) return `${totalSeconds}s`;
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  if (minutes < 60) return `${minutes}m${seconds.toString().padStart(2, '0')}s`;
  const hours = Math.floor(minutes / 60);
  return `${hours}h${(minutes % 60).toString().padStart(2, '0')}m`;
}

export function cliLabel(cli: string): string {
  const value = cli.trim().toLowerCase();
  return value === 'codex' ? 'Codex'
    : value === 'claude' ? 'Claude'
      : value === 'gemini' ? 'Gemini'
        : value === 'copilot' ? 'Copilot'
          : cli;
}

export function stepPrefix(kind: string): string {
  return kind.toLowerCase() === 'aspect' ? 'Review aspect · ' : '';
}
