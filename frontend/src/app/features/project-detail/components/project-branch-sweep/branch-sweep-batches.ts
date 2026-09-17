import type { BranchSweepCandidate, BranchSweepExecutionItem } from '../../../git';

/**
 * Refs per delete request. The backend pushes at most this many ref deletions
 * per `git push`, so sending the same size keeps one request equal to one push
 * and makes the progress read-out honest.
 */
export const BRANCH_SWEEP_BATCH_SIZE = 100;

/** Progress of a running bulk reclaim, shown next to the stop button. */
export interface BranchSweepBatchProgress {
  /** Refs the operator confirmed for this run. */
  total: number;
  /** Refs whose batch has already come back. */
  processed: number;
  deleted: number;
  kept: number;
  batches: number;
  /** 1-based index of the batch currently in flight, 0 before the first. */
  currentBatch: number;
  stopped: boolean;
}

export const EMPTY_BATCH_PROGRESS: BranchSweepBatchProgress = {
  total: 0,
  processed: 0,
  deleted: 0,
  kept: 0,
  batches: 0,
  currentBatch: 0,
  stopped: false,
};

/** Splits confirmed refs into fixed-size batches, preserving order. */
export function toBatches<T>(items: readonly T[], size = BRANCH_SWEEP_BATCH_SIZE): T[][] {
  const bounded = Math.max(1, size);
  const batches: T[][] = [];
  for (let index = 0; index < items.length; index += bounded) {
    batches.push(items.slice(index, index + bounded));
  }
  return batches;
}

/** The execute payload for a set of candidates: ref plus the tip the operator saw. */
export function toExecutionItems(
  candidates: readonly BranchSweepCandidate[],
): BranchSweepExecutionItem[] {
  return candidates.map(candidate => ({ ref: candidate.ref, tipSha: candidate.tipSha }));
}

/** Whole-percent completion, clamped to 0..100 so an empty run reads as done. */
export function batchPercent(progress: BranchSweepBatchProgress): number {
  if (progress.total <= 0) return 100;
  return Math.min(100, Math.round((progress.processed / progress.total) * 100));
}
