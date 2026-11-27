import type { DeviceLoginStatusResponse, ProviderAuthBadge } from '../../../remote-hosts/models/provider-auth.model';
import type { StatusTone } from '@studio/ui/components/status-indicator/status-indicator.component';
import type { ProjectRegistryEntry } from '../../../remote-hosts/models/deployment-checkpoints';

export interface Probe<T> { value: T | null; error: boolean }
export interface ProofCard { id: string; projectId: string; watchPath?: string; installationId: string }
export interface NetworkProof {
  taskId: string;
  attempts: readonly { run: { resultSha?: string | null; finishedAt?: string | null } }[];
  integrations: readonly { reviewAttemptId?: string | null; status: string; integratedSha?: string | null }[];
}
/** A session of the shared Task Server device sign-in service, labelled for this checklist. */
export interface DeviceLogin extends DeviceLoginStatusResponse {
  sessionKey: string;
  hostName: string;
  providerLabel: string;
}
export interface HostJoinToken { joinToken: string; runnerId: string; expiresAt: string }
export interface GitSetup {
  id: string; runnerId?: string; repositoryUrl?: string; method?: 'token' | 'deploy-key';
  state: string; publicKey: string | null; fetchVerified: boolean; pushVerified: boolean;
}

export function providerBadgeWaitReason(badge: ProviderAuthBadge, hostConnected: boolean, nowMs: number): string | null {
  if (badge.state !== 'ok') return null;
  if (!hostConnected) return 'Host has no fresh heartbeat.';
  if (!badge.reachable) return 'Provider probe cannot reach this host.';
  return !badge.lastSeenAt || nowMs - Date.parse(badge.lastSeenAt) >= 5 * 60_000
    ? 'Provider probe is stale.' : null;
}

export function providerBadgeReady(badge: ProviderAuthBadge, nowMs: number): boolean {
  return badge.reachable && badge.state === 'ok' && !!badge.lastSeenAt
    && nowMs - Date.parse(badge.lastSeenAt) < 5 * 60_000;
}

/** Host Git setup state on the shared indicator: waiting on the operator warns, in-flight work runs. */
export function gitSetupTone(state: string): StatusTone {
  if (state === 'verified') return 'success';
  if (state === 'denied' || state === 'failed' || state === 'expired') return 'danger';
  if (state === 'awaiting-github' || state === 'awaiting-verify') return 'warning';
  return 'running';
}

/** Device sign-in session state on the shared indicator. */
export function deviceLoginTone(state: DeviceLoginStatusResponse['state']): StatusTone {
  if (state === 'completed') return 'success';
  return state === 'failed' ? 'danger' : 'running';
}

export function credentialFreeRepositoryUrl(value: string): boolean {
  const trimmed = value.trim();
  if (/^git@[A-Za-z0-9.-]+:[A-Za-z0-9._/-]+$/.test(trimmed)
      && !trimmed.includes('..')) return true;
  try {
    const url = new URL(trimmed);
    return ['https:', 'ssh:'].includes(url.protocol)
      && !!url.hostname && !!url.pathname.replaceAll('/', '')
      && (url.protocol !== 'ssh:' ? !url.username : !url.username || url.username === 'git')
      && !url.password && !url.search && !url.hash;
  } catch { return false; }
}

/** AGT-3809: the checklist's chosen project, else the first active one. */
export function pickProject(projects: readonly ProjectRegistryEntry[], id: string | null): ProjectRegistryEntry | null {
  return projects.find(project => project.id === id) ?? projects[0] ?? null;
}

export function savedProof(): ProofCard | null {
  try {
    const value = JSON.parse(localStorage.getItem('studio-first-run-proof') ?? 'null') as ProofCard | null;
    return value?.id && value.projectId && value.installationId ? value : null;
  } catch { return null; }
}

export function downloadTextFile(name: string, text: string): void {
  const blobUrl = URL.createObjectURL(new Blob([text], { type: 'text/plain' }));
  const link = document.createElement('a');
  link.href = blobUrl;
  link.download = name;
  link.click();
  URL.revokeObjectURL(blobUrl);
}
