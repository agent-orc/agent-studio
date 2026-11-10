import { laneName } from '../../../../models/lane-presentation';
import { TaskState, type TaskInfo } from '../../../../models/task.model';

/**
 * What the Post Processing lane holds, split the way the footer review entry
 * reports it (AGT-3589). The board lane is the truth for what waits: the
 * auto-review queue snapshot only knows the cards that already entered the
 * local slot queue, so it can read "0 waiting" while the lane holds ten cards.
 * `reviewing + waiting + stalled + integration + unclassified === total`, the R3 sum invariant against the
 * lane count the board draws.
 */
export interface StatusBarReviewLane {
  total: number;
  /** A review executor holds the card or a review step is running. */
  reviewing: number;
  /** Waiting for a review attempt or review slot. */
  waiting: number;
  /** Nothing will pick the card up again; an operator decision is needed. */
  stalled: number;
  /** Review passed; delivery integration or recovery is still pending. */
  integration: number;
}

export function summarizeStatusBarReviewLane(cards: readonly TaskInfo[]): StatusBarReviewLane {
  let reviewing = 0;
  let waiting = 0;
  let stalled = 0;
  let integration = 0;
  for (const card of cards) {
    const live = card.liveStatus;
    if (live?.activeStep || live?.queue?.state === 'reviewing') reviewing++;
    else if (live?.queue?.state === 'stalled') stalled++;
    else if (live?.queue?.state === 'integration') integration++;
    else if (live?.queue?.kind === 'review') waiting++;
  }
  return { total: cards.length, reviewing, waiting, stalled, integration };
}

/** One sentence for the review entry tooltip; the counts add up to the lane count. */
export function describeStatusBarReviewLane(lane: StatusBarReviewLane): string {
  const name = laneName(TaskState.AutoReview);
  if (lane.total === 0) return `${name} is empty.`;
  const unclassified = lane.total - lane.waiting - lane.reviewing - lane.stalled - lane.integration;
  const parts = [
    `${lane.waiting} waiting for review`,
    `${lane.reviewing} under review`,
    lane.stalled > 0 ? `${lane.stalled} stalled, operator decision needed` : null,
    lane.integration > 0 ? `${lane.integration} waiting for integration` : null,
    unclassified > 0 ? `${unclassified} in post-processing` : null,
  ].filter((part): part is string => part !== null);
  return `${name} holds ${lane.total} ${lane.total === 1 ? 'card' : 'cards'}: ${parts.join(', ')}.`;
}
