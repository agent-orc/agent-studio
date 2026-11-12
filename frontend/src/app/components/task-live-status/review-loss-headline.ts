import type { TaskLiveQueue } from '../../models/task.model';
import { withRouteSegment } from '../../services/url-hash.util';

/** The CURRENT headline split around the host name, so the host renders as a link. */
export interface ReviewLossHeadline {
  /** The whole sentence, for tests, tooltips and screen readers. */
  text: string;
  before: string;
  host: string | null;
  after: string;
  /** Route of the host detail; null when the loss names no host. */
  hostRoute: string | null;
}

/**
 * AGT-3690: the CURRENT line of a review that was lost on its host. The
 * playthrough card said "Reviewing on first-run-review since 02:26" and then
 * parked; the restart and the automatic retry it scheduled were visible only
 * as a timeline row. This names the cause, the host, when it happened, and the
 * retry that comes next: "Review lost: review host restarted (lab-review-host,
 * 02:27 AM). Automatic retry 1 of 3 at 02:28 AM."
 *
 * Returns null when the queue carries no loss, so older payloads keep their
 * previous wording.
 */
export function reviewLossHeadline(
  queue: TaskLiveQueue | null | undefined,
  clock: (epochMs: number) => string,
): ReviewLossHeadline | null {
  const lost = queue?.lost;
  if (!queue || !lost) return null;

  const cause = lost.cause === 'host-restarted'
    ? 'review host restarted'
    : lost.classification
      ? `review infrastructure failed (${lost.classification})`
      : 'review infrastructure failed';
  const host = lost.hostId?.trim() || lost.executorId?.trim() || null;
  const lostAt = epoch(lost.at);
  const when = lostAt === null ? null : clock(lostAt);

  let next: string;
  const retry = queue.retry;
  const dueAt = epoch(retry?.dueAt);
  if (queue.state === 'retry-scheduled' && retry && dueAt !== null) {
    next = retry.number != null
      ? ` Automatic retry ${retry.number} of ${retry.budget} at ${clock(dueAt)}.`
      : ` Automatic retry at ${clock(dueAt)}.`;
  } else {
    next = ' No automatic retry is scheduled · operator decision needed';
  }

  const before = host || when ? `Review lost: ${cause} (` : `Review lost: ${cause}.`;
  const after = host || when
    ? `${host && when ? ', ' : ''}${when ?? ''}).${next}`
    : next;
  const routeId = lost.executorId?.trim() || lost.hostId?.trim() || null;
  return {
    text: `${before}${host ?? ''}${after}`,
    before,
    host,
    after,
    hostRoute: host && routeId
      ? `/workspace/settings/environment?host=${encodeURIComponent(routeId)}`
      : null,
  };
}

function epoch(value: string | null | undefined): number | null {
  if (!value) return null;
  const parsed = Date.parse(value);
  return Number.isNaN(parsed) ? null : parsed;
}

/** AGT-3690: the host link inside a card opens the host, not the card. */
export function openReviewLossHost(event: MouseEvent, route: string): void {
  event.stopPropagation();
  event.preventDefault();
  window.location.hash = withRouteSegment(window.location.hash, route);
}
