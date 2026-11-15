import type { BatchGateState } from './batch-gate.model';

export interface BatchIsolationHold { flight: string; step: string | null; dueAtUtc: string | null }

/** A conservative preview of the server's live membership guard. The server still checks each mutation. */
export function batchIsolationHold(state: BatchGateState | null, ...keys: (string | null | undefined)[]): BatchIsolationHold | null {
  const taskKey = keys.find(key => key && state?.pending.some(item => item.taskKey === key));
  if (!state || !taskKey) return null;
  const parent = state.batches.find(batch => !batch.parentBatchId && batch.phase === 'red'
    && batch.isolationActive === true && batch.members.some(member => member.taskKey === taskKey && !member.released));
  if (!parent) return null;
  if (state.isolationFindings?.some(item => item.parentBatchId === parent.batchId && item.taskKey === taskKey
    && (item.status === 'proven-red' || item.status === 'superseded'))) return null;
  const claim = state.flightClaim;
  const claimedBatch = state.batches.find(batch => batch.batchId === claim?.batchId);
  const liveClaim = claim && !claim.end && (claim.batchId === parent.batchId
    || claimedBatch?.parentBatchId === parent.batchId) ? claim : null;
  return { flight: parent.batchId, step: liveClaim?.stage ?? null,
    dueAtUtc: liveClaim?.stageDeadlineUtc ?? null };
}
