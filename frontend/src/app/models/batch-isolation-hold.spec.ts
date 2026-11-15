import { describe, expect, it } from 'vitest';
import { batchIsolationHold } from './batch-isolation-hold';
import { redHalvingState } from '../features/batch-gate/testing/batch-fixtures';

describe('batchIsolationHold', () => {
  const state = { ...redHalvingState, pending: [{ ...redHalvingState.pending[0], taskKey: 'AGT-2' }], batches: redHalvingState.batches.map(batch =>
    batch.batchId === 'batch-7' ? { ...batch, isolationActive: true } : batch) };

  it('names a held member and the active step deadline', () => {
    const flight = { batchId: 'batch-8', stage: 'gating', stageDeadlineUtc: '2026-11-15T15:00:00Z', end: null };
    expect(batchIsolationHold({ ...state, flightClaim: flight }, 'AGT-2')).toEqual({
      flight: 'batch-7', step: 'gating', dueAtUtc: '2026-11-15T15:00:00Z',
    });
    expect(batchIsolationHold({ ...state, flightClaim: flight }, '/workspace::AGT-2', 'AGT-2')?.flight).toBe('batch-7');
  });

  it('releases the hold when the flight ends or the member leaves', () => {
    expect(batchIsolationHold(state, 'AGT-2')).toEqual({ flight: 'batch-7', step: null, dueAtUtc: null });
    expect(batchIsolationHold({ ...state, batches: state.batches.map(batch =>
      batch.batchId === 'batch-7' ? { ...batch, isolationActive: false } : batch) }, 'AGT-2')).toBeNull();
    expect(batchIsolationHold({ ...state, pending: [] }, 'AGT-2')).toBeNull();
    expect(batchIsolationHold({ ...state, isolationFindings: [{ parentBatchId: 'batch-7', taskKey: 'AGT-2',
      status: 'proven-red', evidencePath: '', recordedAtUtc: '', provingBatchId: 'batch-8',
      provingRunId: 'run-8', evidenceAvailable: false, failures: [] }] }, 'AGT-2')).toBeNull();
    expect(batchIsolationHold(state, 'AGT-1')).toBeNull();
  });
});
