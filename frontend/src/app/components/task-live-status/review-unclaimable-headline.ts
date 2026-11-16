import type { TaskLiveQueue } from '../../models/task.model';
import { executionHostRoute } from '../../models/remedy-action.model';
import { heldCauseLabel } from '../../models/held-capability-cause';
import type { ReviewLossHeadline } from './review-loss-headline';

/**
 * AGT-3840: the CURRENT line of a review the executor refused to claim. The
 * playthrough card read "Review host saturated: load/core 2.0 > 1.0" for 20
 * minutes while provider-auth:codex was missing, and reviews under the same
 * load passed. This names the key and the host, and the host links to its
 * Execution Hosts row focused on that key:
 * "Review held: provider-auth:codex missing on kori-review-01 · probe indeterminate".
 *
 * Returns null when the queue names no refused key.
 */
export function reviewUnclaimableHeadline(queue: TaskLiveQueue | null | undefined): ReviewLossHeadline | null {
  const unclaimable = queue?.unclaimable;
  const keys = unclaimable?.capabilities?.filter(key => !!key?.trim()) ?? [];
  if (!unclaimable || keys.length === 0) return null;
  const host = unclaimable.hostId?.trim() || unclaimable.executorId?.trim() || null;
  const routeId = unclaimable.executorId?.trim() || unclaimable.hostId?.trim() || null;
  const before = `Review held: ${keys.join(', ')} missing on `;
  const after = ` · ${heldCauseLabel(unclaimable.cause)}`;
  return {
    text: `${before}${host ?? 'the review executor'}${after}`,
    before,
    host: host ?? 'the review executor',
    after,
    hostRoute: routeId ? executionHostRoute(routeId, keys[0]) : null,
    overdue: false,
  };
}
