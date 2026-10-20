import { describe, expect, it } from 'vitest';
import type { RemoteHostCapabilityHealth } from './remote-host.model';
import { hostZonePolicy } from './zone-policy';

function capability(patch: Partial<RemoteHostCapabilityHealth>): RemoteHostCapabilityHealth {
  return {
    key: 'zone-policy',
    category: 'zone',
    advertisedStatus: 'ready',
    healthState: 'healthy',
    advertisedAt: '2026-10-20T09:31:00Z',
    freshUntil: '2026-10-20T09:36:00Z',
    isFresh: true,
    consecutiveFailures: 0,
    version: '2026-10-20.1',
    identity: 'work',
    detail: 'verifiedAt=2026-10-20T09:30:00Z; networks=agent-work; rules=sha256:0123456789ab',
    affectedClaims: [],
    recoveryHistory: [],
    ...patch,
  };
}

describe('hostZonePolicy', () => {
  it('shows the zone, version and verification time of a verified host', () => {
    expect(hostZonePolicy({ capabilityHealth: [capability({})] })).toEqual({
      state: 'verified', label: 'work · 2026-10-20.1', detail: 'Verified 2026-10-20 09:30 UTC',
    });
  });

  it('shows the first failed check when the verification failed', () => {
    const detail = 'verification failed at 2026-10-20T10:30:00Z: 1 of 9 checks; blocked 1.1.1.1:443 expected blocked, observed connected';
    expect(hostZonePolicy({ capabilityHealth: [capability({ advertisedStatus: 'unavailable', detail })] })).toEqual({
      state: 'failed', label: 'Verification failed · 2026-10-20.1', detail,
    });
    expect(hostZonePolicy({ capabilityHealth: [capability({ advertisedStatus: 'unavailable', version: null, detail: null })] }))
      .toEqual({ state: 'failed', label: 'Verification failed', detail: null });
  });

  it('names a record that no longer counts as not verified', () => {
    const detail = 'recorded before the last reboot (verified at 2026-10-20T09:30:00Z); the rules and ipsets are gone';
    expect(hostZonePolicy({ capabilityHealth: [capability({ advertisedStatus: 'unavailable', detail })] })).toEqual({
      state: 'failed', label: 'Not verified · 2026-10-20.1', detail,
    });
  });

  it('marks a stale report and a host that reports no zone policy', () => {
    expect(hostZonePolicy({ capabilityHealth: [capability({ isFresh: false })] }).state).toBe('stale');
    expect(hostZonePolicy({ capabilityHealth: [capability({ key: 'git:fetch' })] })).toEqual({
      state: 'not-reported', label: 'Not reported', detail: null,
    });
    expect(hostZonePolicy({}).state).toBe('not-reported');
  });
});
