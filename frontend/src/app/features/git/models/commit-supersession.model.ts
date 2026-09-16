import type { TaskCommitInfo } from './git.model';

/**
 * AGT-2817 - `next-attempt` is a placeholder, not a verdict.
 *
 * The backend stamps `supersededByAttempt: 'next-attempt'` between an explicit
 * requeue and the publication of the replacement attempt, then resolves it to
 * the real attempt id. When no replacement ever publishes - the card is
 * integrated by an operator merge, or the requeue is abandoned - the
 * placeholder stays. AGT-2706 shipped and stayed marked, so every consumer
 * that read supersession as a verdict showed the delivered work as replaced.
 *
 * These three states are the distinction the UI has to keep:
 *
 * - `current`: no marker; the card's live delivery.
 * - `replacement-pending`: requeued, replacement not published yet. Still the
 *   only delivery the card has, so it stays in the current list.
 * - `replaced`: a named successor exists (a replacement SHA or a resolved
 *   attempt id). Only this one is history.
 */
export type CommitSupersessionState = 'current' | 'replacement-pending' | 'replaced';

/** The literal placeholder value the backend writes. Mirrors `TaskCommitSupersession.PendingAttempt`. */
export const PENDING_ATTEMPT = 'next-attempt';

export function commitSupersessionState(commit: TaskCommitInfo): CommitSupersessionState {
  if (commit.supersededBySha?.trim()) return 'replaced';
  const attempt = commit.supersededByAttempt?.trim();
  if (!attempt) return 'current';
  return attempt === PENDING_ATTEMPT ? 'replacement-pending' : 'replaced';
}

/** A commit with a named successor. The only state that means "this was replaced". */
export function isReplacedCommit(commit: TaskCommitInfo): boolean {
  return commitSupersessionState(commit) === 'replaced';
}

/** Commits that still count towards the card's current delivery. */
export function isEffectiveDelivery(commit: TaskCommitInfo): boolean {
  return !isReplacedCommit(commit);
}
