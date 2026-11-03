import type { DeviceLoginStatusResponse } from '../../../remote-hosts/models/provider-auth.model';
import type { StatusTone } from '../../../../components/status-indicator/status-indicator.component';

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
