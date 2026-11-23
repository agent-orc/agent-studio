import type { DeployView } from './project-deploy-area';

/** Reads the incident's requested Deploy view when opening a project link. */
export function linkedDeployView(): DeployView | null {
  const hash = window.location.hash;
  if (!hash.includes('/deploy?')) return null;
  const requested = new URLSearchParams(hash.split('?')[1] ?? '').get('view');
  return requested === 'operations' || requested === 'runbooks' || requested === 'tools' ? requested : null;
}
