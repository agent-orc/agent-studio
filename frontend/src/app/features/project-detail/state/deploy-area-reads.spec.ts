import { describe, expect, it } from 'vitest';
import { deployInFlight, deploymentEnvironment, deployRunConfirmation } from './deploy-area-reads';
import type { DeployRun, EnvironmentCardView, EnvironmentStatus, Release } from './deploy-area.model';
import type { ProjectEnvironments } from '../../../models/project-overview.model';

const run = (state: DeployRun['state']) => ({
  id: '0e89a55e02fda97b', environmentId: 'production', environmentName: 'Production',
  releaseId: 'rel-3', releaseVersion: '2026.10.19.2', state,
}) as DeployRun;
const release = (cardKeys: string[]) => ({ id: 'rel-3', version: '2026.10.19.2', cardKeys }) as Release;

describe('deploy area reads (F17)', () => {
  it('names the environment, the release version, and its card count, never the run id', () => {
    expect(deployRunConfirmation('approve', run('awaiting-approval'), [release(['AGT-1', 'AGT-2'])]))
      .toBe('Approve deploying release 2026.10.19.2 (2 cards) to Production?');
    expect(deployRunConfirmation('reject', run('awaiting-approval'), [release(['AGT-1'])]))
      .toBe('Reject deploying release 2026.10.19.2 (1 card) to Production?');
    expect(deployRunConfirmation('abort', run('running'), []))
      .toBe('Abort deploying release 2026.10.19.2 (card list not loaded) to Production?');
  });

  it('counts queued and running runs and unreported host deploys as in flight', () => {
    expect(deployInFlight([run('queued')], null)).toBe(true);
    expect(deployInFlight([run('running')], null)).toBe(true);
    expect(deployInFlight([run('succeeded'), run('awaiting-approval')], null)).toBe(false);
    const pending = { environments: [{ pendingArtifact: 'web@sha256:8f3e0d237096' }] } as unknown as ProjectEnvironments;
    expect(deployInFlight([run('succeeded')], pending)).toBe(true);
  });

  it('names a deploy action environment by its card and knows Production by kind or by name', () => {
    const cards = [{ id: 'live', name: 'Live' }, { id: 'stage', name: 'Staging' }] as EnvironmentCardView[];
    const statuses = { status: 'ready' as const, value: [{ environment: { id: 'live', kind: 'production' } }] as EnvironmentStatus[] };
    const none = { status: 'loading' as const };
    expect(deploymentEnvironment('live', cards, statuses, none)).toEqual({ name: 'Live', production: true });
    expect(deploymentEnvironment('stage', cards, statuses, none)).toEqual({ name: 'Staging', production: false });
    expect(deploymentEnvironment('production', [], none, none)).toEqual({ name: 'production', production: true });
  });
});
