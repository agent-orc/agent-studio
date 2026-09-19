import { worstDrift } from './host-release-drift';
import type { HostHeartbeatStatus, RemoteHost, RunnerServiceRole } from './remote-host.model';

export interface PhysicalHostGroup {
  /** Stable machine identity advertised by the runner capability contract. */
  id: string;
  name: string;
  machine: RemoteHost;
  roles: readonly RemoteHost[];
}

/** Group process identities under their advertised physical machine. */
export function groupPhysicalHosts(
  hosts: readonly RemoteHost[],
  includeRetired: boolean,
): PhysicalHostGroup[] {
  const visible = includeRetired ? hosts : hosts.filter(host => host.status !== 'retired');
  const buckets = new Map<string, RemoteHost[]>();
  for (const host of visible) {
    const id = physicalHostId(host);
    buckets.set(id, [...(buckets.get(id) ?? []), host]);
  }

  return [...buckets.entries()].map(([id, bucket]) => {
    const roles = [...bucket].sort(compareRoles);
    const detailRole = roles.find(role => role.serviceRole === 'coding')
      ?? roles.find(role => role.serviceRole === 'local')
      ?? roles[0];
    const telemetryRole = [...roles].sort(
      (left, right) => observationTime(right) - observationTime(left),
    )[0];
    const latestRole = [...roles].sort(
      (left, right) => heartbeatTime(right) - heartbeatTime(left),
    )[0];
    const releaseDrift = worstDrift(roles.map(role => role.releaseDrift));
    // `roles` is already in deterministic role order, so worstDrift's stable
    // tie handling prefers coding before review. Keep the complete aggregate
    // release identity attached to the role that supplied its drift verdict.
    const releaseRole = releaseDrift
      ? roles.find(role => role.releaseDrift === releaseDrift) ?? detailRole
      : latestRole;
    const name = detailRole.capacityHostId?.trim() || detailRole.name;
    const machine: RemoteHost = {
      ...detailRole,
      name,
      status: aggregateStatus(roles),
      lastHeartbeatAt: latestRole.lastHeartbeatAt,
      releaseId: releaseRole.releaseId ?? null,
      release: releaseRole.release ?? null,
      // One late role makes the machine late: the aggregate must not read
      // calmer than the rows it summarises (R3).
      releaseDrift,
      stats: telemetryRole.stats,
      telemetry: telemetryRole.telemetry,
      telemetryLoading: roles.some(role => role.telemetryLoading),
    };
    return { id, name, machine, roles };
  });
}

export function physicalHostId(host: RemoteHost): string {
  const advertised = host.capacityHostId?.trim();
  if (advertised) return advertised;
  return host.role === 'local' ? `local:${host.id}` : `runner:${host.id}`;
}

function compareRoles(left: RemoteHost, right: RemoteHost): number {
  const compared = roleOrder(left.serviceRole) - roleOrder(right.serviceRole);
  return compared || left.name.localeCompare(right.name, undefined, { numeric: true });
}

function roleOrder(role: RunnerServiceRole | undefined): number {
  switch (role) {
    case 'local': return 0;
    case 'coding': return 1;
    case 'review': return 2;
    case 'runner':
    default: return 3;
  }
}

function aggregateStatus(roles: readonly RemoteHost[]): HostHeartbeatStatus {
  if (roles.every(role => role.status === 'retired')) return 'retired';
  if (roles.every(role => role.status === 'offline')) return 'offline';
  if (roles.some(role => role.status === 'degraded' || role.status === 'offline')) return 'degraded';
  if (roles.some(role => role.status === 'draining')) return 'draining';
  if (roles.some(role => role.status === 'online')) return 'online';
  return 'idle';
}

function observationTime(host: RemoteHost): number {
  const point = host.telemetry?.points.at(-1)?.timestamp;
  return timestamp(point ?? host.lastHeartbeatAt);
}

function heartbeatTime(host: RemoteHost): number {
  return timestamp(host.lastHeartbeatAt);
}

function timestamp(value: string | null | undefined): number {
  if (!value) return 0;
  const parsed = Date.parse(value);
  return Number.isFinite(parsed) ? parsed : 0;
}
