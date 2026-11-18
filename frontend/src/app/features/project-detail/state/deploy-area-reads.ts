import type { WritableSignal } from '@angular/core';
import type { ProjectEnvironments } from '../../../models/project-overview.model';
import type { DeployPage } from '../services/project-deploy-data.service';
import type { DeployPanel, DeployRun, DeployRunAction, EnvironmentStatus, Release } from './deploy-area.model';

/** Pure helpers for the Deploy area's reads and their live refresh (F17). */

/** F17: while a run is queued or running, cards and history follow the host within 5 s. */
export const DEPLOY_LIVE_REFRESH_MS = 3_000;

/** A run the host has still to finish, or a host deploy that has not reported yet. */
export function deployInFlight(runs: readonly DeployRun[], environments: ProjectEnvironments | null): boolean {
  return runs.some(run => run.state === 'queued' || run.state === 'running')
    || !!environments?.environments.some(item => item.pendingArtifact);
}

export function isProductionRun(run: DeployRun, statuses: readonly EnvironmentStatus[] | null, environments: ProjectEnvironments | null): boolean {
  return !!statuses?.some(item => item.environment.id === run.environmentId && item.environment.kind === 'production')
    || !!environments?.environments.some(item => item.declaration.id === run.environmentId && item.declaration.kind === 'production')
    || run.environmentName.toLowerCase() === 'production';
}

/** F17: the confirmation names the environment, the release version, and its card count, not the run id. */
export function deployRunConfirmation(action: DeployRunAction, run: DeployRun, releases: readonly Release[]): string {
  const verb = action === 'cancel' || action === 'abort' ? 'Abort' : action === 'approve' ? 'Approve' : 'Reject';
  const release = releases.find(item => item.id === run.releaseId);
  const count = release?.cardKeys.length ?? 0;
  const subject = release ? `release ${release.version} (${count} ${count === 1 ? 'card' : 'cards'})`
    : `release ${run.releaseVersion ?? run.releaseId} (card list not loaded)`;
  return `${verb} deploying ${subject} to ${run.environmentName}?`;
}

/** With `keepShown`, ready data stays on screen until the reload answers. */
export function intoPanel<T>(target: WritableSignal<DeployPanel<T>>, keepShown: boolean): (panel: DeployPanel<T>) => void {
  return panel => {
    if (!keepShown || panel.status !== 'loading' || target().status !== 'ready') target.set(panel);
  };
}

export function pageItems<T>(panel: DeployPanel<DeployPage<T>>): DeployPanel<T[]> {
  return panel.status === 'ready' ? { status: 'ready', value: panel.value.items } : panel;
}

/** One notice per failed source, named for the reader. */
export function deployNotices(sources: readonly (readonly [string, DeployPanel<unknown>])[]): string[] {
  return sources.flatMap(([name, panel]) => panel.status === 'error' ? [`${name}: ${panel.message}`] : []);
}
