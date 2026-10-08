import type { RemoteHost } from './remote-host.model';

export interface InstallationIdentity {
  installationId: string;
  ownerBootstrapped: boolean;
  ownerBootstrapArmed: boolean;
}

export interface RegisteredProject {
  projectId: string;
  workspaceId: string;
  name: string;
}

export interface ProjectPlacement {
  requiredCapabilities: readonly string[];
  pinnedRunnerId: string | null;
  maxParallelism: number;
}

export interface RepositoryStatus {
  registration: {
    projectId: string;
    repositoryUrl: string;
    integrationRef: string;
    releaseRef: string | null;
    deliveryPolicy: string;
  };
  probes: readonly {
    runnerId: string;
    admitted: boolean;
    verdict: string;
    detail: string | null;
    observedAt: string;
  }[];
}

export interface ProjectRepositoryRead {
  project: RegisteredProject;
  status: RepositoryStatus | null;
  error: string | null;
  placement: ProjectPlacement | null;
  placementError: string | null;
}

export interface FullBackupSummary {
  id: string;
  createdAt: string;
  taskCount: number;
  coldPayloadCount: number;
  setSha256: string;
  warnings: readonly string[];
}

/** Only the latest receipt from one runner may admit its project. */
export function latestRepositoryProbes(status: RepositoryStatus): RepositoryStatus['probes'] {
  const latest = new Map<string, RepositoryStatus['probes'][number]>();
  for (const probe of status.probes) {
    const old = latest.get(probe.runnerId);
    if (!old || Date.parse(probe.observedAt) > Date.parse(old.observedAt))
      latest.set(probe.runnerId, probe);
  }
  return [...latest.values()].sort((a, b) => a.runnerId.localeCompare(b.runnerId));
}

export function hostForProbe(hosts: readonly RemoteHost[], runnerId: string): RemoteHost | undefined {
  return hosts.find(host => host.clientId === runnerId || host.id === runnerId);
}

export function repositoryProofLabel(
  probe: RepositoryStatus['probes'][number], hosts: readonly RemoteHost[], now: number,
): string {
  const host = hostForProbe(hosts, probe.runnerId);
  if (!probe.admitted) return `${probe.verdict}: ${probe.detail || 'Origin access was not admitted.'} Check the host Git credentials and run the repository probe again.`;
  if (!host || host.status !== 'online' || !host.lastHeartbeatAt
      || now - Date.parse(host.lastHeartbeatAt) > 90_000)
    return 'Proof admitted, host disconnected. Reconnect and refresh its capabilities.';
  return 'Last origin proof admitted; host connected. Re-probe to refresh.';
}
