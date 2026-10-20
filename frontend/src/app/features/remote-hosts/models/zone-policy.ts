import type { RemoteHostCapabilityHealth } from './remote-host.model';

/** Capability key the coding, review and gate daemons report (AGT-3231). */
export const ZONE_POLICY_CAPABILITY = 'zone-policy';

export interface HostZonePolicy {
  state: 'verified' | 'failed' | 'stale' | 'not-reported';
  label: string;
  detail: string | null;
}

/**
 * The zone network policy a host enforces, from its `zone-policy` capability:
 * zone and policy version after a passed verification, the first failed check
 * after a failed one, and the reason for any other record that no longer
 * counts (an earlier boot, a stopped timer, a removal). A host that never
 * applied the policy reports no capability.
 */
export function hostZonePolicy(host: { capabilityHealth?: readonly RemoteHostCapabilityHealth[] }): HostZonePolicy {
  const capability = host.capabilityHealth?.find(entry => entry.key === ZONE_POLICY_CAPABILITY);
  if (!capability) return { state: 'not-reported', label: 'Not reported', detail: null };
  const version = capability.version ?? 'unknown version';
  if (capability.advertisedStatus !== 'ready') {
    const failed = !capability.detail || capability.detail.startsWith('verification failed');
    const label = failed ? 'Verification failed' : 'Not verified';
    return {
      state: 'failed',
      label: capability.version ? `${label} · ${capability.version}` : label,
      detail: capability.detail ?? null,
    };
  }
  const label = `${capability.identity ?? 'work'} · ${version}`;
  if (!capability.isFresh) return { state: 'stale', label, detail: 'Last report is stale' };
  const verifiedAt = /verifiedAt=(\d{4}-\d{2}-\d{2})T(\d{2}:\d{2})/.exec(capability.detail ?? '');
  return { state: 'verified', label, detail: verifiedAt ? `Verified ${verifiedAt[1]} ${verifiedAt[2]} UTC` : null };
}
