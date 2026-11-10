import { describe, expect, it } from 'vitest';
import { claimGateEventDetail, claimGateEventTitle, claimLoadGateLabel, hostsNotClaiming, notClaimingLabel } from './claim-load-gate';
import type { RemoteHost } from './remote-host.model';

describe('claimLoadGateLabel', () => {
  it('states why free slots cannot claim', () => {
    expect(claimLoadGateLabel({ state: 'closed', loadPerCore: 5.51, threshold: 4,
      sustainedSeconds: 702, since: '2026-11-05T12:00:00Z', activeSlots: 7 }))
      .toBe('Not claiming: load 5.5 per core above 4.0 for 12 min');
  });

  it('keeps the open state accurate during a rising load window', () => {
    expect(claimLoadGateLabel({ state: 'open', loadPerCore: 4.4, threshold: 4,
      sustainedSeconds: 0, since: null, activeSlots: 7 }))
      .toContain('waiting for sustained threshold');
  });
});

describe('claim gate history and fleet helpers', () => {
  const event = { sequence: 1, occurredAt: '2026-11-05T12:00:00Z', runnerId: 'runner-a', hostId: 'host-a',
    loadPerCore: 5.51, threshold: 4, activeSlots: 4 };

  it('states load and duration for a stop and a resume', () => {
    const stopped = { ...event, event: 'claim-stopped' as const, durationSeconds: 702 };
    const resumed = { ...event, event: 'claim-resumed' as const, loadPerCore: 3.2, durationSeconds: 1380 };
    expect(claimGateEventTitle(stopped)).toBe('Claim stopped');
    expect(claimGateEventDetail(stopped)).toBe('Load 5.5 per core above 4.0 for 12 min');
    expect(claimGateEventTitle(resumed)).toBe('Claim resumed');
    expect(claimGateEventDetail(resumed)).toBe('Not claiming for 23 min · load 3.2 per core, threshold 4.0');
    expect(claimGateEventDetail({ ...resumed, durationSeconds: 0 })).toContain('Stop duration not reported');
  });

  it('orders hosts that are not claiming by how long their gate has been closed', () => {
    const host = (name: string, since: string | null, state: 'open' | 'closed' = 'closed'): RemoteHost => ({
      id: name, name, role: 'remote', clientId: name, address: null, status: 'online', os: 'Linux',
      lastHeartbeatAt: null, uptimeLabel: null, capabilities: [], cliQuotas: [], stats: null,
      claimLoadGate: { state, loadPerCore: 5, threshold: 4, sustainedSeconds: 600, since, activeSlots: 1 },
    });
    expect(hostsNotClaiming([host('late', '2026-11-05T12:10:00Z'), host('open', null, 'open'),
      host('early', '2026-11-05T12:00:00Z')]).map(item => item.name)).toEqual(['early', 'late']);
    expect(notClaimingLabel(1)).toBe('1 host not claiming');
    expect(notClaimingLabel(2)).toBe('2 hosts not claiming');
  });
});
