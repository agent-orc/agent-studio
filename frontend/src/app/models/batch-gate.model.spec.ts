import { describe, expect, it } from 'vitest';
import { isolatedRedState, redHalvingState } from '../features/batch-gate/testing/batch-fixtures';
import {
  batchAlert, batchIsolationEvidenceUrl, batchIsolationRows, batchProvenRedFor, batchShortId, batchWaitingCards,
  type BatchGateState,
} from './batch-gate.model';

const root = (state: BatchGateState) => state.batches.find(batch => batch.batchId === 'batch-14')!;

describe('batch waiting projection', () => {
  it('excludes members of a formed or running batch from pending records', () => {
    const pending = [1, 2].map(index => ({ taskKey: `AGT-${index}`, reviewAttemptId: `r-${index}`,
      resultSha: 'a'.repeat(40), waitMinutes: 229.234388035, risk: 0.2 }));
    const batch = { ...redHalvingState.batches[1], members: [{ taskKey: 'AGT-1', replay: 'applied', released: false }] };
    for (const phase of ['formed', 'running']) {
      const state = { ...redHalvingState, pending, batches: [{ ...batch, phase }], members: {} };
      expect(batchWaitingCards(state).map(item => item.taskKey)).toEqual(['AGT-2']);
    }
  });
});

describe('batch isolation model (AGT-3577)', () => {
  it('lists every member of a bisected red batch as proven red or proven green', () => {
    const rows = batchIsolationRows(isolatedRedState, root(isolatedRedState))!;
    expect(rows.map(row => [row.taskKey, row.status])).toEqual([
      ['AGT-1', 'proven-green'], ['AGT-2', 'proven-red'], ['AGT-3', 'proven-green'], ['AGT-4', 'proven-green'],
    ]);
    expect(rows[1].finding?.provingRunId).toBe('run-17');
  });

  it('names a member without a finding as not yet isolated and skips child and green batches', () => {
    const state = { ...isolatedRedState, isolationFindings: isolatedRedState.isolationFindings!
      .filter(finding => finding.taskKey !== 'AGT-1') };
    expect(batchIsolationRows(state, root(state))![0]).toMatchObject({ taskKey: 'AGT-1', status: 'not-isolated', finding: null });
    expect(batchIsolationRows(state, state.batches[1])).toBeNull();
    expect(batchIsolationRows({ ...state, isolationFindings: [] }, { ...root(state), phase: 'published' })).toBeNull();
  });

  it('keeps the newest finding per member', () => {
    const state = { ...isolatedRedState, isolationFindings: [...isolatedRedState.isolationFindings!,
      { parentBatchId: 'batch-14', taskKey: 'AGT-2', status: 'superseded', evidencePath: '', recordedAtUtc: '2026-11-09T10:00:00Z' }] };
    expect(batchIsolationRows(state, root(state))![1].status).toBe('superseded');
  });

  it('reports a red batch with its culprit until a newer batch publishes', () => {
    expect(batchAlert(isolatedRedState)).toEqual({ state: 'red', batchId: 'batch-14', culprits: ['AGT-2'] });
    const moved = { ...isolatedRedState, batches: [
      { ...isolatedRedState.batches[0], batchId: 'batch-19', parentBatchId: null, phase: 'published' },
      ...isolatedRedState.batches] };
    expect(batchAlert(moved)).toBeNull();
  });

  it('reports isolating while a child of the red batch is active', () => {
    expect(batchAlert(redHalvingState)).toEqual({ state: 'isolating', batchId: 'batch-7', culprits: [] });
    expect(batchAlert({ ...redHalvingState, batches: redHalvingState.batches.map(batch =>
      batch.batchId === 'batch-7' ? { ...batch, isolationActive: false } : batch) }))
      .toEqual({ state: 'red', batchId: 'batch-7', culprits: [] });
    expect(batchAlert({ ...redHalvingState, batches: [] })).toBeNull();
    expect(batchAlert(null)).toBeNull();
  });

  it('marks only the current delivery of a card proven red', () => {
    const culprit = isolatedRedState.members['AGT-2'];
    expect(batchProvenRedFor(isolatedRedState, culprit, '/tmp/x::AGT-2', 'AGT-2')?.provingBatchId).toBe('batch-17');
    expect(batchProvenRedFor(isolatedRedState, isolatedRedState.members['AGT-1'], 'AGT-1')).toBeNull();
    // A later delivery waits outside a batch with a new result SHA.
    const later = { ...isolatedRedState, pending: [{ taskKey: 'AGT-2', reviewAttemptId: 'review-2', resultSha: '9'.repeat(40), waitMinutes: 1, risk: 0.2 }] };
    expect(batchProvenRedFor(later, { status: 'pending' }, 'AGT-2')).toBeNull();
  });

  it('builds the evidence link and shortens generated batch ids', () => {
    const finding = isolatedRedState.isolationFindings![2];
    expect(batchIsolationEvidenceUrl('my project', finding))
      .toBe('/api/projects/my%20project/batch-gate/isolation-evidence?parentBatchId=batch-14&taskKey=AGT-2');
    expect(batchShortId('0123456789abcdef0123456789abcdef')).toBe('01234567');
    expect(batchShortId('batch-14')).toBe('batch-14');
  });
});
