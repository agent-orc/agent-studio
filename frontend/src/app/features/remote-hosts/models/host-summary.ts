import { hostReadiness } from './host-readiness';
import type { PhysicalHostGroup } from './physical-host-group';
import type { HostHeartbeatStatus } from './remote-host.model';

/** The summary bucket of one machine row; it follows the status the row displays. */
export type HostRowBucket = 'reporting' | 'degraded' | 'draining' | 'offline' | 'retired';

export interface HostSummaryCounts {
  machines: number;
  reporting: number;
  degraded: number;
  draining: number;
  offline: number;
  retired: number;
  /** Machine rows tagged "Not enrolled"; they also count once in their status bucket. */
  notEnrolled: number;
  /** Role rows plus gate worker rows. */
  roles: number;
}

export function hostRowBucket(status: HostHeartbeatStatus): HostRowBucket {
  switch (status) {
    case 'online':
    case 'idle': return 'reporting';
    case 'degraded': return 'degraded';
    case 'draining': return 'draining';
    case 'offline': return 'offline';
    case 'retired': return 'retired';
  }
}

/** Every role of the machine lacks a Task Server host identity. */
export function groupNotEnrolled(group: PhysicalHostGroup, now: number): boolean {
  return group.roles.every(role => hostReadiness(role, now).state === 'not-enrolled');
}

/**
 * Header tallies of the Hosts list (R3): each machine row lands in exactly the
 * bucket of the status label it shows, so the buckets add up to the machine
 * rows. A gate worker without a runner machine renders its own "Online" row.
 */
export function summarizeHostRows(
  groups: readonly PhysicalHostGroup[],
  gates: readonly { name: string }[],
  now: number,
): HostSummaryCounts {
  const counts: HostSummaryCounts = {
    machines: 0, reporting: 0, degraded: 0, draining: 0, offline: 0, retired: 0, notEnrolled: 0, roles: 0,
  };
  for (const group of groups) {
    counts.machines++;
    counts[hostRowBucket(group.machine.status)]++;
    if (groupNotEnrolled(group, now)) counts.notEnrolled++;
    counts.roles += group.roles.length;
  }
  for (const gate of gates) {
    counts.roles++;
    if (groups.some(group => group.name === gate.name)) continue;
    counts.machines++;
    counts.reporting++;
  }
  return counts;
}
