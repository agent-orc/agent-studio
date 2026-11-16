import type { StatusTone } from '../../components/status-indicator/status-indicator.component';
import { projectRepositoryUrl } from '../../models/project-basics.model';
import type { RegistryProjectSummary } from '../../models/task.model';

/** Mirrors backend `ProjectServerCheckoutStatus` (AGT-3809). */
export type ServerCheckoutState = 'present' | 'missing' | 'unavailable' | 'running' | 'ready' | 'failed';

export interface ServerCheckoutStatus {
  projectId: string;
  state: ServerCheckoutState;
  step: 'clone' | 'checkout' | 'register' | null;
  repositoryUrl: string | null;
  repositoryPath: string | null;
  branch: string | null;
  error: string | null;
}

export interface ServerCheckoutView {
  state: ServerCheckoutState;
  tone: StatusTone;
  headline: string;
  detail: string;
  path: string | null;
  canStart: boolean;
}

/** A registered repository URL with no checkout on the Studio server. */
export function serverCheckoutMissing(project: RegistryProjectSummary): boolean {
  return !project.archived && !!projectRepositoryUrl(project).trim() && !project.repositoryPath?.trim();
}

const STEP_LABEL: Record<NonNullable<ServerCheckoutStatus['step']>, string> = {
  clone: 'Cloning the repository',
  checkout: 'Checking out the integration branch',
  register: 'Registering the checkout',
};

/**
 * The notice for one project, or null when it has nothing to say. A live job
 * wins over the registry fact, so progress shows while the registry still
 * reports the project without a checkout.
 */
export function serverCheckoutView(project: RegistryProjectSummary, status: ServerCheckoutStatus | null): ServerCheckoutView | null {
  const url = status?.repositoryUrl || projectRepositoryUrl(project);
  switch (status?.state) {
    case 'running': return {
      state: 'running', tone: 'info', headline: 'Creating server checkout',
      detail: `${STEP_LABEL[status.step ?? 'clone']}${status.step === 'checkout' && status.branch ? ` ${status.branch}` : ''}…`,
      path: null, canStart: false,
    };
    case 'ready': return {
      state: 'ready', tone: 'success', headline: 'Server checkout created',
      detail: `${project.displayName} integrates from this checkout${status.branch ? ` on ${status.branch}` : ''}.`,
      path: status.repositoryPath, canStart: false,
    };
    case 'failed': return {
      state: 'failed', tone: 'danger', headline: 'Server checkout failed',
      detail: status.error ?? 'The server checkout could not be created.', path: null, canStart: true,
    };
  }
  if (status?.state === 'present' || status?.state === 'unavailable' || !serverCheckoutMissing(project)) return null;
  return {
    state: 'missing', tone: 'warning', headline: 'No server checkout',
    detail: `Cards in ${project.displayName} cannot integrate until Studio clones ${url} on this server.`,
    path: null, canStart: true,
  };
}
