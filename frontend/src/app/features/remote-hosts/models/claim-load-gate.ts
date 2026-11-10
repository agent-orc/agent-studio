import type { ClaimLoadGate, RemoteHost } from './remote-host.model';

/** One recorded claim gate transition (`GET /api/v1/management/claim-gate-events`). */
export interface ClaimGateEvent {
  sequence: number;
  occurredAt: string;
  runnerId: string;
  hostId: string | null;
  event: 'claim-stopped' | 'claim-resumed';
  loadPerCore: number;
  threshold: number;
  /** Seconds load stayed high before the stop, or seconds claims stayed stopped before the resume. */
  durationSeconds: number;
  activeSlots: number;
}

function minutes(seconds: number): string {
  return `${Math.max(1, Math.round(seconds / 60))} min`;
}

export function claimLoadGateLabel(gate: ClaimLoadGate): string {
  const load = gate.loadPerCore.toFixed(1);
  const threshold = gate.threshold.toFixed(1);
  if (gate.state === 'closed') {
    return `Not claiming: load ${load} per core above ${threshold} for ${minutes(gate.sustainedSeconds)}`;
  }
  return gate.loadPerCore <= gate.threshold
    ? `Claiming: load ${load} per core below ${threshold}`
    : `Claiming: load ${load} per core above ${threshold}, waiting for sustained threshold`;
}

/** Live roles whose claim gate is closed, the longest-closed first. */
export function hostsNotClaiming(hosts: readonly RemoteHost[]): RemoteHost[] {
  const since = (host: RemoteHost) => Date.parse(host.claimLoadGate?.since ?? '') || Number.MAX_SAFE_INTEGER;
  return hosts
    .filter(host => host.status !== 'retired' && host.claimLoadGate?.state === 'closed')
    .sort((left, right) => since(left) - since(right) || left.name.localeCompare(right.name));
}

export function notClaimingLabel(count: number): string {
  return `${count} ${count === 1 ? 'host' : 'hosts'} not claiming`;
}

export function claimGateEventTitle(event: ClaimGateEvent): string {
  return event.event === 'claim-stopped' ? 'Claim stopped' : 'Claim resumed';
}

/** Load and duration of one transition, in the words the host card uses. */
export function claimGateEventDetail(event: ClaimGateEvent): string {
  const load = event.loadPerCore.toFixed(1);
  const threshold = event.threshold.toFixed(1);
  if (event.event === 'claim-stopped') {
    return `Load ${load} per core above ${threshold} for ${minutes(event.durationSeconds)}`;
  }
  const closed = event.durationSeconds > 0
    ? `Not claiming for ${minutes(event.durationSeconds)}`
    : 'Stop duration not reported';
  return `${closed} · load ${load} per core, threshold ${threshold}`;
}
