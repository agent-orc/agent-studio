import { signal } from '@angular/core';
import { vi } from 'vitest';
import type { OperatorCatalogue, StandingGrant } from '../models/standing-grant.model';

export function operatorCatalogue(): OperatorCatalogue {
  return {
    operations: [
      { id: 'observe', tier: 'T0', scopeKind: 'workspace', title: 'Read board, cards, runs, logs, metrics, host status, audit and credential metadata' },
      { id: 'card.requeue', tier: 'T1', scopeKind: 'project', title: 'Requeue a card' },
      { id: 'card.steer', tier: 'T1', scopeKind: 'project', title: "Steer a card's run" },
      { id: 'service.restart', tier: 'T1', scopeKind: 'host', title: 'Restart an allowlisted service' },
      { id: 'stage.deploy', tier: 'T1', scopeKind: 'environment', title: 'Deploy a gate-verified commit to stage' },
      { id: 'secrets.change', tier: 'T2', scopeKind: 'project', title: 'Change secrets' },
      { id: 'gate.bypass', tier: 'T2', scopeKind: 'project', title: 'Bypass a gate' },
    ],
    defaultGrantDays: 7,
    maxGrantDays: 30,
    defaultRatePerHour: 10,
    maxRatePerHour: 120,
  };
}

export function standingGrant(overrides: Partial<StandingGrant> = {}): StandingGrant {
  return {
    id: 'sgr_live',
    principalId: 'opa_ops',
    principalName: 'studio-operator',
    tier: 'T1',
    operations: ['card.requeue', 'card.steer'],
    scope: { kind: 'project', project: 'PROJ-001' },
    ratePerHour: 10,
    window: null,
    issuedBy: 'usr_owner',
    issuedAt: '2026-11-02T08:00:00Z',
    expiresAt: '2026-11-09T08:00:00Z',
    renewedAt: null,
    renewedBy: null,
    revokedAt: null,
    revokedBy: null,
    reason: 'Requeue stalled cards overnight',
    revokeReason: null,
    state: 'active',
    usesLastHour: 3,
    ...overrides,
  };
}

/** A signal-backed stand-in for StandingGrantsService with spy mutations. */
export function fakeStandingGrantsService(grants: StandingGrant[] = [standingGrant()]) {
  return {
    catalogue: signal<OperatorCatalogue | null>(operatorCatalogue()),
    grants: signal<readonly StandingGrant[]>(grants),
    state: signal<'idle' | 'loading' | 'ready' | 'denied' | 'error'>('ready'),
    busy: signal(false),
    error: signal<string | null>(null),
    load: vi.fn(() => Promise.resolve()),
    issue: vi.fn(() => Promise.resolve(standingGrant({ id: 'sgr_new' }))),
    renew: vi.fn(() => Promise.resolve(standingGrant())),
    revoke: vi.fn(() => Promise.resolve(standingGrant({ state: 'revoked' }))),
  };
}
