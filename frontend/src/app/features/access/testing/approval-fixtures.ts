import { signal } from '@angular/core';
import { vi } from 'vitest';
import type { OperatorApproval } from '../models/operator-approval.model';

export function operatorApproval(overrides: Partial<OperatorApproval> = {}): OperatorApproval {
  return {
    id: 'apr_wait',
    principalId: 'opa_ops',
    principalName: 'studio-operator',
    sessionId: 'ses_morning',
    actor: 'operator-agent:opa_ops/session:ses_morning',
    delegatedBy: 'usr_owner',
    operation: 'data.delete',
    operationTitle: 'Delete data',
    tier: 'T2',
    method: 'DELETE',
    path: '/api/tasks/AGT-12',
    query: '?project=PROJ-001',
    parameters: null,
    parameterDigest: `sha256:${'4c1d'.repeat(16)}`,
    target: 'project:PROJ-001/task:AGT-12',
    instructionSource: 'owner-chat',
    reason: 'Remove the duplicate card',
    requestedAt: '2026-11-04T09:00:00Z',
    expiresAt: '2026-11-04T09:15:00Z',
    state: 'pending',
    decidedBy: null,
    decidedAt: null,
    decisionNote: null,
    approvalExpiresAt: null,
    usedAt: null,
    resultStatus: null,
    cancelledAt: null,
    cancelReason: null,
    ...overrides,
  };
}

/** A signal-backed stand-in for OperatorApprovalsService with a spy decision. */
export function fakeOperatorApprovalsService(approvals: OperatorApproval[] = [operatorApproval()]) {
  return {
    approvals: signal<readonly OperatorApproval[]>(approvals),
    state: signal<'idle' | 'loading' | 'ready' | 'denied' | 'error'>('ready'),
    busy: signal(false),
    error: signal<string | null>(null),
    load: vi.fn(() => Promise.resolve()),
    decide: vi.fn(() => Promise.resolve(operatorApproval({ state: 'approved' }))),
  };
}
