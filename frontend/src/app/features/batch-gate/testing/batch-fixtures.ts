import type { BatchGateConfiguration, BatchGateState } from '../../../models/batch-gate.model';

export const batchSettings: BatchGateConfiguration = {
  settings: {
    enabled: true, scope: 'code', planner: 'look-ahead', minimumSize: 2, maximumSize: 4,
    deadlineMinutes: 30, lookAheadMinutes: 15, urgentTags: ['urgent'], riskPolicy: 'balanced',
    overlap: 'separate', reviewTestLevel: 'work-package', gateTimeoutMinutes: 90,
    advisor: { mode: 'enforce', cliType: 'codex', model: 'test-model', timeoutSeconds: 45 },
  },
  limits: { hardMaximumSize: 8 },
};

export const redHalvingState: BatchGateState = {
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
