import { Injectable, InjectionToken, inject } from '@angular/core';
import type { Deployment, Environment, EnvironmentStatus, Release } from '../models/deployment.model';

export const DEPLOYMENT_CLOCK = new InjectionToken<() => number>('deployment clock', {
  providedIn: 'root', factory: () => Date.now,
});

/** Fixture boundary. BD-1 replaces this producer without changing the view contract. */
@Injectable({ providedIn: 'root' })
export class DeploymentStatusService {
  private readonly now = inject(DEPLOYMENT_CLOCK);

  statuses(projectId: string, seed: number): EnvironmentStatus[] {
    const now = this.now();
    const at = (hoursAgo: number) => new Date(now - hoursAgo * 3_600_000).toISOString();
    const version = (revision: number) => `2026.10.${String(new Date(now).getUTCDate()).padStart(2, '0')}.${revision}`;
    const release = (revision: number, hoursAgo: number): Release => ({
      id: `${projectId}-release-${revision}`, projectId, version: version(revision),
      commitSha: `${(seed + 1).toString(16).padStart(7, '0')}${revision}`,
      artifactDigests: [], processVersion: '1', variablesVersion: '1',
      cardKeys: [], createdAtUtc: at(hoursAgo), channel: 'standard',
    });
    const latest = release(3, 2 + seed);
    const previous = release(2, 27 + seed);
    const environments: Environment[] = [
      { id: `${projectId}-staging`, projectId, name: 'Staging', order: 0, kind: 'staging' },
      { id: `${projectId}-production`, projectId, name: 'Production', order: 1, kind: 'production' },
    ];
    const deployment = (environment: Environment, current: Release, state: Deployment['state'], hoursAgo: number): Deployment => ({
      id: `${environment.id}-deployment`, releaseId: current.id, environmentId: environment.id,
      state, startedAtUtc: at(hoursAgo), finishedAtUtc: state === 'succeeded' || state === 'failed' ? at(hoursAgo - 0.1) : null,
      approvedBy: null, steps: [],
    });
    return environments.map(environment => {
      const staging = environment.kind === 'staging';
      const state: Deployment['state'] = staging ? (seed % 4 === 2 ? 'failed' : 'succeeded')
        : (seed % 3 === 1 ? 'awaiting-approval' : 'succeeded');
      const currentRelease = staging ? latest : previous;
      const deploymentRelease = state === 'awaiting-approval' ? latest : currentRelease;
      return {
        environment, currentRelease,
        currentDeployment: deployment(environment, deploymentRelease, state, staging ? 2 + seed : 27 + seed),
        newerRelease: staging ? null : latest,
        cardsSinceLastDeploy: [],
        health: staging && state === 'failed' ? 'unhealthy' : 'healthy',
      };
    });
  }
}
