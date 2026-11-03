import { signal } from '@angular/core';
import { vi } from 'vitest';
import type {
  AuditEntry,
  OneTimeOperatorSession,
  OperatorActivity,
  OperatorAgent,
  OperatorSession,
} from '../models/operator-agent.model';

/** One-time session tokens are assembled at runtime so no credential-shaped literal lands in source. */
export function operatorSessionToken(agentId = 'opa_ops', sessionId = 'ses_new'): string {
  return ['oas', agentId, sessionId, Math.random().toString(36).slice(2)].join('.');
}

export function operatorSession(overrides: Partial<OperatorSession> = {}): OperatorSession {
  const id = overrides.id ?? 'ses_live';
  const agentId = overrides.operatorAgentId ?? 'opa_ops';
  return {
    id,
    operatorAgentId: agentId,
    name: 'Release check',
    actor: `operator-agent:${agentId}/session:${id}`,
    delegatedBy: 'usr_owner',
    createdAt: '2026-11-02T08:00:00Z',
    createdBy: 'usr_owner',
    expiresAt: '2026-11-02T16:00:00Z',
    renewedAt: null,
    lastUsedAt: '2026-11-02T08:30:00Z',
    revokedAt: null,
    state: 'active',
    ...overrides,
  };
}

export function operatorAgents(): OperatorAgent[] {
  return [
    {
      id: 'opa_ops', name: 'studio-operator', description: 'Runs release checks', createdAt: '2026-11-01T09:00:00Z',
      createdBy: 'usr_owner', disabledAt: null,
      sessions: [
        operatorSession({ id: 'ses_old', name: null, state: 'expired', createdAt: '2026-11-01T09:00:00Z', expiresAt: '2026-11-01T17:00:00Z', lastUsedAt: null }),
        operatorSession(),
      ],
    },
    {
      id: 'opa_off', name: 'retired-operator', description: null, createdAt: '2026-10-20T09:00:00Z',
      createdBy: 'usr_owner', disabledAt: '2026-10-21T09:00:00Z',
      sessions: [operatorSession({ id: 'ses_gone', operatorAgentId: 'opa_off', name: 'Old run', state: 'revoked', revokedAt: '2026-10-21T09:00:00Z' })],
    },
  ];
}

export function auditEntry(sequence: number, overrides: Partial<AuditEntry> = {}): AuditEntry {
  return {
    sequence,
    at: '2026-11-02T08:30:00Z',
    stream: 'iam',
    actor: 'operator-agent:opa_ops/session:ses_live',
    session: 'ses_live',
    delegatedBy: 'usr_owner',
    operation: 'task.move',
    target: 'PROJ-001/AGT-1',
    parameterDigest: `sha256-${'a1b2c3d4'.repeat(8)}`,
    outcome: 'succeeded',
    hash: `h${sequence}`,
    previousHash: `h${sequence - 1}`,
    ...overrides,
  };
}

export function operatorActivity(overrides: Partial<OperatorActivity> = {}): OperatorActivity {
  return {
    operatorAgents: operatorAgents(),
    entries: [auditEntry(42), auditEntry(41, { operation: 'session.start', target: null, parameterDigest: null })],
    integrity: { state: 'verified', checkedAt: '2026-11-02T08:45:00Z', verifiedThrough: 42, message: null, alert: false },
    forwarding: {
      sinkKind: 'syslog', configured: true, headSequence: 42, forwardedSequence: 40, pending: 2,
      lastForwardedAt: '2026-11-02T08:40:00Z', oldestPendingAt: '2026-11-02T08:41:00Z', lastError: null, alert: false,
    },
    ...overrides,
  };
}

export function oneTimeOperatorSession(): OneTimeOperatorSession {
  return {
    operatorAgentId: 'opa_ops', sessionId: 'ses_new', token: operatorSessionToken(),
    actor: 'operator-agent:opa_ops/session:ses_new', delegatedBy: 'usr_owner', expiresAt: '2026-11-02T17:00:00Z',
  };
}

/** A signal-backed stand-in for OperatorAgentsService with spy mutations. */
export function fakeOperatorAgentsService(agents: OperatorAgent[] = operatorAgents(), activity: OperatorActivity | null = operatorActivity()) {
  const issued = oneTimeOperatorSession();
  const sessionFilter = signal<string | null>(null);
  return {
    issuedToken: issued.token,
    agents: signal<readonly OperatorAgent[]>(agents),
    state: signal<'idle' | 'loading' | 'ready' | 'denied' | 'error'>('ready'),
    available: signal(true),
    busy: signal(false),
    error: signal<string | null>(null),
    activity: signal<OperatorActivity | null>(activity),
    activityState: signal<'idle' | 'loading' | 'ready' | 'denied' | 'error'>('ready'),
    activityError: signal<string | null>(null),
    sessionFilter,
    verifying: signal(false),
    load: vi.fn(() => Promise.resolve()),
    loadActivity: vi.fn(() => Promise.resolve()),
    filterSession: vi.fn((sessionId: string | null) => {
      sessionFilter.set(sessionId);
      return Promise.resolve();
    }),
    verify: vi.fn(() => Promise.resolve(null)),
    create: vi.fn(() => Promise.resolve({})),
    disable: vi.fn(() => Promise.resolve({})),
    startSession: vi.fn(() => Promise.resolve(issued)),
    renewSession: vi.fn(() => Promise.resolve({})),
    revokeSession: vi.fn(() => Promise.resolve({})),
  };
}
