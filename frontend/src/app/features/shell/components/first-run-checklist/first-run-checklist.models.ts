import type { DeviceLoginStatusResponse } from '../../../remote-hosts/models/provider-auth.model';

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
