/**
 * Test-only Deploy area data (component specs and the mocked Playwright spec).
 * App code never imports this file, so it is not part of a production build.
 *
 * Story: release 2026.10.18.1 (artifact A) runs in Staging and Production.
 * Staging then tried an unreleased artifact C, failed its health check, and
 * rolled back to A automatically; release 2026.10.19.1 (artifact B) is now
 * healthy in Staging and awaits approval for Production.
 */
import type { ProjectEnvironments } from '../../../models/project-overview.model';
import type { DeployHealthCheckSummary, DeployLifecycleRead, Release } from '../state/deploy-area.model';

export const DEPLOY_FIXTURE_NOW = Date.parse('2026-10-19T12:00:00Z');

const digest = (fill: string) => `sha256:${fill.repeat(64)}`;
export const ARTIFACT_A = `registry.example/website@${digest('a')}`;
export const ARTIFACT_B = `registry.example/website@${digest('b')}`;
export const ARTIFACT_C = `registry.example/website@${digest('c')}`;

const declaration = (id: string, name: string, kind: 'stage' | 'production') => ({
  id, name, kind, targetHostId: 'web-vm', address: `https://${id}.website.example`, template: 'static' as const,
  destination: `/srv/www/${id}`, containerPort: 8080, healthUrl: `https://${id}.website.example/healthz`,
  approval: 'person' as const, secretRefs: [],
});

const job = (id: string, environmentId: string, artifact: string, status: string, createdAt: string,
  claimedAt: string | null, requestedBy: string, isRollback = false) => ({
  id, environmentId, hostId: 'web-vm', artifact, previousArtifact: null, status, createdAt, claimedAt, isRollback, requestedBy,
});

export function deployEnvironmentsFixture(): ProjectEnvironments {
  return {
    project: 'Website',
    environments: [
      {
        declaration: declaration('stage', 'Staging', 'stage'), deployedArtifact: ARTIFACT_B, health: 'healthy', pendingArtifact: null,
        history: [
          { at: '2026-10-19T11:48:00Z', status: 'healthy', artifact: ARTIFACT_B, previousArtifact: ARTIFACT_A, evidence: 'health-ok' },
          { at: '2026-10-19T08:02:00Z', status: 'rolled-back', artifact: ARTIFACT_A, previousArtifact: ARTIFACT_A, evidence: 'health-ok' },
          { at: '2026-10-19T08:00:00Z', status: 'failed', artifact: ARTIFACT_C, previousArtifact: ARTIFACT_A, evidence: 'health-http-failed' },
          { at: '2026-10-18T09:00:00Z', status: 'healthy', artifact: ARTIFACT_A, previousArtifact: null, evidence: 'health-ok' },
        ],
      },
      {
        declaration: declaration('production', 'Production', 'production'), deployedArtifact: ARTIFACT_A, health: 'healthy', pendingArtifact: null,
        history: [
          { at: '2026-10-18T22:10:00Z', status: 'healthy', artifact: ARTIFACT_A, previousArtifact: null, evidence: 'health-ok' },
        ],
      },
    ],
    promotions: [
      { artifact: ARTIFACT_A, stageId: 'stage', productionId: 'production', stageHealthyAt: '2026-10-18T09:00:00Z', status: 'deployed', actor: 'local.owner' },
      { artifact: ARTIFACT_B, stageId: 'stage', productionId: 'production', stageHealthyAt: '2026-10-19T11:48:00Z', status: 'pending', actor: null, expiresAt: '2026-10-20T11:48:00Z' },
    ],
    jobs: [
      job('job-a-stage', 'stage', ARTIFACT_A, 'healthy', '2026-10-18T08:58:00Z', '2026-10-18T08:59:00Z', 'release-gate'),
      job('job-a-production', 'production', ARTIFACT_A, 'healthy', '2026-10-18T22:05:00Z', '2026-10-18T22:06:00Z', 'local.owner'),
      job('job-c-stage', 'stage', ARTIFACT_C, 'failed', '2026-10-19T07:58:00Z', '2026-10-19T07:59:00Z', 'release-gate'),
      job('job-a-stage-rollback', 'stage', ARTIFACT_A, 'healthy', '2026-10-19T08:00:00Z', '2026-10-19T08:01:00Z', 'automatic-rollback', true),
      job('job-b-stage', 'stage', ARTIFACT_B, 'healthy', '2026-10-19T11:45:00Z', '2026-10-19T11:46:00Z', 'release-gate'),
    ],
    failureCards: [
      { environmentId: 'stage', artifact: ARTIFACT_C, at: '2026-10-19T08:00:00Z', evidence: 'health-http-failed', rollbackArtifact: ARTIFACT_A },
    ],
  };
}

export function deployReleasesFixture(): Release[] {
  const release = (version: string, artifact: string, commit: string, cardKeys: string[], createdAtUtc: string): Release => ({
    id: `release-${version}`, projectId: 'PROJ-WEB', version, commitSha: commit.repeat(40).slice(0, 40),
    artifactDigests: [artifact.slice(artifact.lastIndexOf('@') + 1)], processVersion: `project.yml@${commit.repeat(7)}`,
    variablesVersion: `project.yml@${commit.repeat(7)}`, cardKeys, createdAtUtc, channel: 'standard',
  });
  return [
    release('2026.10.18.1', ARTIFACT_A, 'a', ['WEB-29', 'WEB-30'], '2026-10-18T08:50:00Z'),
    release('2026.10.19.1', ARTIFACT_B, 'b', ['WEB-33', 'WEB-34', 'WEB-35'], '2026-10-19T11:40:00Z'),
  ];
}

export function deployHealthFixture(): DeployHealthCheckSummary[] {
  return [{ environmentId: 'stage', state: 'healthy', checkCount: 2, checkedAtUtc: '2026-10-19T11:59:00Z' }];
}

export function deployLifecycleFixture(): DeployLifecycleRead {
  return {
    valid: true,
    lifecycle: {
      declared: true,
      nodes: [
        { id: 'gate', kind: 'gate', name: 'Gate', items: ['npm run build', 'npm test'], automatic: true, approval: [], requires: null },
        { id: 'artifact', kind: 'artifact', name: 'Artifact', items: ['web (static, dist)'], automatic: true, approval: [], requires: 'gate' },
        { id: 'staging', kind: 'environment', name: 'staging', items: ['publish-static: web to web-vm (deploy/website)', 'health: /healthz'],
          automatic: true, approval: [], requires: 'artifact' },
        { id: 'production', kind: 'environment', name: 'production', items: ['publish-static: web to web-vm (deploy/website)'],
          automatic: false, approval: ['Owner', 'Maintainer'], requires: 'staging' },
      ],
    },
  };
}
