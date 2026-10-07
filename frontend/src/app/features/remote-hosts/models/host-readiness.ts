import type { RemoteHost, RemoteHostCapabilityHealth } from './remote-host.model';

export interface HostReadiness {
  state: 'not-enrolled' | 'enrolled' | 'connected' | 'capability-ready';
  reason: string;
  action: string;
}

function ready(capability: RemoteHostCapabilityHealth): boolean {
  return capability.isFresh && capability.advertisedStatus === 'ready'
    && capability.healthState === 'healthy';
}

/** A host role's observable progression; project eligibility also needs H2 origin proof. */
export function hostReadiness(host: RemoteHost, now: number): HostReadiness {
  if (!host.runnerInstanceId || !host.capacityHostId) return {
    state: 'not-enrolled', reason: 'No Task Server host identity and daemon instance reported.',
    action: 'Enrol this host and wait for its first capability advertisement.',
  };
  if (host.status === 'offline' || host.status === 'retired' || !host.lastHeartbeatAt
      || !Number.isFinite(Date.parse(host.lastHeartbeatAt))
      || now - Date.parse(host.lastHeartbeatAt) > 90_000) return {
    state: 'enrolled', reason: 'The enrolled daemon has no fresh connection.',
    action: 'Restore its Task Server route and refresh the host.',
  };
  if (host.hostAdmission?.admissionState !== 'open') return {
    state: 'connected', reason: `Host admission is ${host.hostAdmission?.admissionState ?? 'unreported'}.`,
    action: 'Inspect the drain or admission reason before resuming claims.',
  };
  const role = host.serviceRole === 'review' ? 'review' : 'coding';
  const needed = [`executor:${role}`, 'git:push'];
  const capabilities = host.capabilityHealth ?? [];
  for (const key of needed) {
    const capability = capabilities.find(item => item.key === key);
    if (!capability || !ready(capability)) return {
      state: 'connected',
      reason: capability && !capability.isFresh
        ? `${key} advertisement is stale.`
        : capability?.reason || capability?.detail || `${key} is missing.`,
      action: `Refresh ${key} on this host and inspect its capability detail.`,
    };
  }
  const provider = capabilities.filter(item => item.key.startsWith('provider-auth:'));
  if (!provider.some(ready)) return {
    state: 'connected', reason: provider[0]?.reason || provider[0]?.detail || 'No fresh provider authentication.',
    action: 'Sign in to a provider as the runner service user, then refresh capabilities.',
  };
  if ((host.roleMaxParallelism ?? host.effectiveMaxParallelism ?? 0) < 1) return {
    state: 'connected', reason: 'No role slot budget is reported.',
    action: 'Set a measured coding or review budget and restart the role daemon.',
  };
  return {
    state: 'capability-ready', reason: 'Fresh role, Git and provider capabilities with an open budget.',
    action: 'Check the registered project origin proof before assigning work.',
  };
}
