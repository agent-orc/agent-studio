import type { BatchGateConfiguration, BatchGateState } from '../../../models/batch-gate.model';

export const batchSettings: BatchGateConfiguration = {
  settings: {
    enabled: true, scope: 'code', planner: 'look-ahead', minimumSize: 2, maximumSize: 4,
    deadlineMinutes: 30, lookAheadMinutes: 15, urgentTags: ['urgent'], riskPolicy: 'balanced', calibration: 'empirical', stacking: 'off',
    overlap: 'separate', reviewTestLevel: 'work-package', gateTimeoutMinutes: 90, preCheckEnabled: true,
    advisor: { mode: 'enforce', cliType: 'codex', model: 'test-model', timeoutSeconds: 45 },
  },
  limits: { hardMaximumSize: 8 },
};

export const redHalvingState: BatchGateState = {
  gateSlots: { busy: 2, total: 3 },
  gateCapacity: { slots: 3, processorsPerGate: 6, hostCores: 24, allowOversubscription: false },
  plan: {
    decision: 'wait', reason: 'Waiting for a safe group', targetSize: 3,
    closeAtUtc: '2026-10-11T14:05:00Z', proposedMembers: ['AGT-1', 'AGT-2'],
    upcoming: [{ taskKey: 'AGT-3', stage: 'in review', etaUtc: '2026-10-11T14:00:00Z', risk: 'high', overlapsWith: ['AGT-1'] }],
    advisor: { mode: 'enforce', verdict: 'veto', reasons: ['Overlapping high-risk changes'], model: 'test-model', atUtc: '2026-10-11T13:55:00Z' },
  },
  pending: [{ taskKey: 'AGT-1', reviewAttemptId: 'review-1', resultSha: 'a'.repeat(40), waitMinutes: 12, risk: 'high' }],
  batches: [{
    batchId: 'batch-7', phase: 'red', phaseReason: 'Suite failed; isolate the culprit', baseSha: 'b'.repeat(40), candidateSha: 'c'.repeat(40),
    members: [{ taskKey: 'AGT-2', replay: 'applied', released: false }],
    runs: [{ batchRunId: 'run-7', host: 'agent-runner-01', outcome: 'failed', classification: 'test-failure', startedAtUtc: '2026-10-11T13:00:00Z', finishedAtUtc: '2026-10-11T13:05:00Z' }],
  }, {
    batchId: 'batch-8', parentBatchId: 'batch-7', phase: 'running', baseSha: 'b'.repeat(40), candidateSha: 'd'.repeat(40),
    formationReason: 'new batch: overlap separate from waiting batch batch-7, files src/shared.cs',
    exclusions: [{ taskKey: 'AGT-3', reason: 'overlapSeparate', detail: 'overlap separate: src/shared.cs' }],
    members: [{ taskKey: 'AGT-2', replay: 'applied', released: false }],
    runs: [{ batchRunId: 'run-8', host: 'agent-runner-02', startedAtUtc: '2026-10-11T13:06:00Z' }],
  }],
  members: {
    'AGT-1': { status: 'pending' },
    'AGT-2': { status: 'in-batch', batchId: 'batch-8', phase: 'running', position: 1, size: 1 },
    'AGT-4': { status: 'fallback' },
  },
};

/**
 * Shape of the AGT-3052 backend state (BatchGateStateSnapshotReader): risk is
 * a probability, replay is an object with an outcome, and absent values are
 * explicit nulls.
 */
export const backendShapedState: BatchGateState = {
  plan: {
    decision: 'wait', reason: 'Waiting for AGT-3 within the look-ahead window', closeAtUtc: null,
    proposedMembers: ['AGT-1'], targetSize: 2,
    upcoming: [{ taskKey: 'AGT-3', stage: 'review', etaUtc: null, risk: 0.35, overlapsWith: [] }],
    advisor: { mode: 'advise', verdict: 'approve', reasons: ['Low overlap'], model: 'test-model', atUtc: '2026-10-11T13:55:00Z' },
  },
  pending: [{ taskKey: 'AGT-1', reviewAttemptId: 'review-1', resultSha: 'a'.repeat(40), waitMinutes: 3, risk: 0.2, exclusion: null }],
  batches: [{
    batchId: 'batch-9', parentBatchId: null, phase: 'published', phaseReason: null, baseSha: 'b'.repeat(40), candidateSha: 'c'.repeat(40),
    members: [{ taskKey: 'AGT-2', replay: { outcome: 'applied' }, released: true }],
    runs: [{ batchRunId: 'run-9', host: 'agent-runner-01', outcome: 'Passed', classification: null, startedAtUtc: '2026-10-11T13:00:00Z', finishedAtUtc: '2026-10-11T13:20:00Z' }],
    publication: { verifiedRemoteSha: 'c'.repeat(40), testedCandidateSha: 'c'.repeat(40) },
  }],
  members: {
    'AGT-1': { status: 'pending', batchId: null, phase: null, position: null, size: null },
    'AGT-2': { status: 'published', batchId: 'batch-9', phase: 'published', position: 1, size: 1 },
  },
};

const run = (id: string, outcome: string, start: string, end: string) =>
  ({ batchRunId: id, host: 'agent-runner-01', outcome, classification: outcome === 'fail' ? 'DeterministicSuiteRed' : null,
    startedAtUtc: start, finishedAtUtc: end });
const member = (taskKey: string, released = false) => ({ taskKey, replay: { outcome: 'admitted' }, released });
const evidence = (batchId: string) => `/var/lib/agentstudio/batch-gates/${batchId}/gate-evidence.jsonl`;

/**
 * AGT-3577: a four-member red batch bisected to one member. AGT-2 is proven
 * red by run-17 of batch-17; the other three are proven green.
 */
export const isolatedRedState: BatchGateState = {
  plan: { decision: 'idle', reason: 'no eligible cards', closeAtUtc: null, proposedMembers: [], targetSize: 0, upcoming: [], advisor: null },
  pending: [],
  batches: [
    { batchId: 'batch-18', parentBatchId: 'batch-14', phase: 'published', baseSha: 'b'.repeat(40), candidateSha: 'e'.repeat(40),
      members: [member('AGT-1', true)], runs: [run('run-18', 'pass', '2026-11-09T09:40:00Z', '2026-11-09T09:52:00Z')],
      publication: { verifiedRemoteSha: 'e'.repeat(40) } },
    { batchId: 'batch-17', parentBatchId: 'batch-14', phase: 'red', baseSha: 'b'.repeat(40), candidateSha: 'f'.repeat(40),
      members: [member('AGT-2')], runs: [run('run-17', 'fail', '2026-11-09T09:40:00Z', '2026-11-09T09:51:00Z')] },
    { batchId: 'batch-16', parentBatchId: 'batch-14', phase: 'published', baseSha: 'b'.repeat(40), candidateSha: 'd'.repeat(40),
      members: [member('AGT-3', true), member('AGT-4', true)], runs: [run('run-16', 'pass', '2026-11-09T09:20:00Z', '2026-11-09T09:33:00Z')],
      publication: { verifiedRemoteSha: 'd'.repeat(40) } },
    { batchId: 'batch-15', parentBatchId: 'batch-14', phase: 'red', baseSha: 'b'.repeat(40), candidateSha: 'a'.repeat(40),
      members: [member('AGT-1'), member('AGT-2')], runs: [run('run-15', 'fail', '2026-11-09T09:20:00Z', '2026-11-09T09:34:00Z')] },
    { batchId: 'batch-14', parentBatchId: null, phase: 'red', phaseReason: 'DeterministicSuiteRed', baseSha: 'b'.repeat(40), candidateSha: 'c'.repeat(40),
      members: [member('AGT-1'), member('AGT-2'), member('AGT-3'), member('AGT-4')],
      runs: [run('run-14', 'fail', '2026-11-09T09:00:00Z', '2026-11-09T09:15:00Z')] },
  ],
  members: {
    'AGT-1': { status: 'published', batchId: 'batch-18', phase: 'published', position: 1, size: 1 },
    'AGT-2': { status: 'in-batch', batchId: 'batch-17', phase: 'red', position: 1, size: 1 },
    'AGT-3': { status: 'published', batchId: 'batch-16', phase: 'published', position: 1, size: 2 },
    'AGT-4': { status: 'published', batchId: 'batch-16', phase: 'published', position: 2, size: 2 },
  },
  isolationFindings: [
    { parentBatchId: 'batch-14', taskKey: 'AGT-3', status: 'proven-green', evidencePath: evidence('batch-16'),
      recordedAtUtc: '2026-11-09T09:33:00Z', provingBatchId: 'batch-16', provingRunId: 'run-16', failures: [], evidenceAvailable: false },
    { parentBatchId: 'batch-14', taskKey: 'AGT-4', status: 'proven-green', evidencePath: evidence('batch-16'),
      recordedAtUtc: '2026-11-09T09:33:00Z', provingBatchId: 'batch-16', provingRunId: 'run-16', failures: [], evidenceAvailable: false },
    { parentBatchId: 'batch-14', taskKey: 'AGT-2', status: 'proven-red', evidencePath: evidence('batch-17'),
      recordedAtUtc: '2026-11-09T09:51:00Z', provingBatchId: 'batch-17', provingRunId: 'run-17', evidenceAvailable: true,
      failures: [
        { testName: 'Checkout.Tests.TotalsIncludeShipping', errorExcerpt: 'Error Message:\n Assert.Equal() Failure: Expected 12.50, Actual 10.00' },
        { testName: 'Checkout.Tests.EmptyCartHasNoTotal', errorExcerpt: '' },
      ] },
    { parentBatchId: 'batch-14', taskKey: 'AGT-1', status: 'proven-green', evidencePath: evidence('batch-18'),
      recordedAtUtc: '2026-11-09T09:52:00Z', provingBatchId: 'batch-18', provingRunId: 'run-18', failures: [], evidenceAvailable: false },
  ],
};
