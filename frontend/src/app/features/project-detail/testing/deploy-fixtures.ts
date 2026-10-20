/**
 * Test-only Deploy area data (component specs and the mocked Playwright spec).
 * App code never imports this file, so it is not part of a production build.
 * Shapes follow the AGT-3176 environments API, the AGT-3201 deploy routes,
 * and the AGT-3221 pipeline-definition route.
 *
 * Story: release 2026.10.18.1 (artifact A) runs in Staging and Production.
 * Release 2026.10.19.1 (artifact B) failed its Staging health check and was
 * rolled back automatically. Release 2026.10.19.2 (artifact C) is healthy in
 * Staging, a redeploy of it was cancelled, and it awaits approval for Production.
 */
import type { ProjectEnvironments } from '../../../models/project-overview.model';
import {
  buildDeployRuns,
  environmentNames,
  type DeployRun,
  type DeploySources,
  type Deployment,
  type DeploymentLogLine,
  type EnvironmentStatus,
  type Release,
} from '../state/deploy-area.model';
import type { PipelineDefinitionResponse } from '../services/project-pipeline-definition.service';

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

export function deployEnvironmentsFixture(): ProjectEnvironments {
  return {
    project: 'Website',
    environments: [
      {
        declaration: declaration('stage', 'Staging', 'stage'), deployedArtifact: ARTIFACT_C, health: 'healthy', pendingArtifact: null,
        history: [
          { at: '2026-10-19T11:48:00Z', status: 'healthy', artifact: ARTIFACT_C, previousArtifact: ARTIFACT_A, evidence: 'health-ok' },
          { at: '2026-10-19T08:02:00Z', status: 'rolled-back', artifact: ARTIFACT_A, previousArtifact: ARTIFACT_A, evidence: 'health-ok' },
          { at: '2026-10-19T08:00:00Z', status: 'failed', artifact: ARTIFACT_B, previousArtifact: ARTIFACT_A, evidence: 'health-http-failed' },
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
      { artifact: ARTIFACT_C, stageId: 'stage', productionId: 'production', stageHealthyAt: '2026-10-19T11:48:00Z', status: 'pending', actor: null },
    ],
    failureCards: [
      { environmentId: 'stage', artifact: ARTIFACT_B, at: '2026-10-19T08:00:00Z', evidence: 'health-http-failed', rollbackArtifact: ARTIFACT_A },
    ],
  };
}

const release = (version: string, artifact: string, fill: string, cardKeys: string[], createdAtUtc: string): Release => ({
  id: fill.repeat(16), projectId: 'Website', version, commitSha: fill.repeat(40),
  artifactDigests: [artifact.slice(artifact.lastIndexOf('@') + 1)], processVersion: fill.repeat(12),
  variablesVersion: `${fill}0`.repeat(6), cardKeys, createdAtUtc, channel: 'default',
});

export const RELEASE_1 = release('2026.10.18.1', ARTIFACT_A, 'a', ['WEB-29', 'WEB-30'], '2026-10-18T08:50:00Z');
export const RELEASE_2 = release('2026.10.19.1', ARTIFACT_B, 'b', ['WEB-33', 'WEB-34'], '2026-10-19T07:50:00Z');
export const RELEASE_3 = release('2026.10.19.2', ARTIFACT_C, 'c', ['WEB-35'], '2026-10-19T11:40:00Z');

/** `GET /deploy/releases`: newest first, as AGT-3201 serves them. */
export function deployReleasesFixture(): Release[] {
  return [RELEASE_3, RELEASE_2, RELEASE_1];
}

type StepTuple = [name: string, state: string, startedAtUtc: string | null, finishedAtUtc: string | null];

const run = (id: string, releaseId: string, environmentId: string, state: string, trigger: string,
  times: { created: string; started?: string; finished?: string }, steps: StepTuple[],
  people: { requestedBy?: string; approvedBy?: string } = {}): Deployment => ({
  id, releaseId, environmentId, state, trigger,
  createdAtUtc: times.created, startedAtUtc: times.started ?? null, finishedAtUtc: times.finished ?? null,
  requestedBy: people.requestedBy ?? null, approvedBy: people.approvedBy ?? null,
  steps: steps.map(([name, stepState, startedAtUtc, finishedAtUtc]) => ({
    name, state: stepState, startedAtUtc, finishedAtUtc, logRef: `${id}/${name}`,
  })),
});

/** `GET /deploy/deployments`: newest first. */
export function deployDeploymentsFixture(): Deployment[] {
  return [
    run('run-c-stage-redeploy', RELEASE_3.id, 'stage', 'cancelled', 'redeploy', { created: '2026-10-19T11:54:00Z', finished: '2026-10-19T11:55:00Z' },
      [['publish-static', 'cancelled', null, null], ['health', 'cancelled', null, null]], { requestedBy: 'local.dev' }),
    run('run-c-production', RELEASE_3.id, 'production', 'awaiting-approval', 'deploy', { created: '2026-10-19T11:49:00Z' },
      [['approval', 'awaiting-approval', '2026-10-19T11:49:00Z', null], ['publish-static', 'pending', null, null], ['health', 'pending', null, null]],
      { requestedBy: 'local.dev' }),
    run('run-c-stage', RELEASE_3.id, 'stage', 'succeeded', 'deploy', { created: '2026-10-19T11:45:00Z', started: '2026-10-19T11:46:00Z', finished: '2026-10-19T11:48:00Z' },
      [['publish-static', 'succeeded', '2026-10-19T11:46:00Z', '2026-10-19T11:48:00Z'], ['health', 'succeeded', '2026-10-19T11:48:00Z', '2026-10-19T11:48:00Z']],
      { requestedBy: 'release-gate' }),
    run('run-a-stage-rollback', RELEASE_1.id, 'stage', 'succeeded', 'automatic-rollback', { created: '2026-10-19T08:00:00Z', started: '2026-10-19T08:01:00Z', finished: '2026-10-19T08:02:00Z' },
      [['publish-static', 'succeeded', '2026-10-19T08:01:00Z', '2026-10-19T08:02:00Z'], ['health', 'succeeded', '2026-10-19T08:02:00Z', '2026-10-19T08:02:00Z']],
      { requestedBy: 'automatic-rollback' }),
    run('run-b-stage', RELEASE_2.id, 'stage', 'failed', 'deploy', { created: '2026-10-19T07:58:00Z', started: '2026-10-19T07:59:00Z', finished: '2026-10-19T08:00:00Z' },
      [['publish-static', 'succeeded', '2026-10-19T07:59:00Z', '2026-10-19T08:00:00Z'], ['health', 'failed', '2026-10-19T08:00:00Z', '2026-10-19T08:00:00Z']],
      { requestedBy: 'release-gate' }),
    run('run-a-production', RELEASE_1.id, 'production', 'succeeded', 'deploy', { created: '2026-10-18T21:00:00Z', started: '2026-10-18T22:06:00Z', finished: '2026-10-18T22:10:00Z' },
      [['approval', 'succeeded', '2026-10-18T21:00:00Z', '2026-10-18T22:05:00Z'], ['publish-static', 'succeeded', '2026-10-18T22:06:00Z', '2026-10-18T22:10:00Z'],
        ['health', 'succeeded', '2026-10-18T22:10:00Z', '2026-10-18T22:10:00Z']],
      { requestedBy: 'release-gate', approvedBy: 'local.owner' }),
    run('run-a-stage', RELEASE_1.id, 'stage', 'succeeded', 'deploy', { created: '2026-10-18T08:58:00Z', started: '2026-10-18T08:59:00Z', finished: '2026-10-18T09:00:00Z' },
      [['publish-static', 'succeeded', '2026-10-18T08:59:00Z', '2026-10-18T09:00:00Z'], ['health', 'succeeded', '2026-10-18T09:00:00Z', '2026-10-18T09:00:00Z']],
      { requestedBy: 'release-gate' }),
  ];
}

const environment = (id: string, name: string, order: number, kind: string) => ({ id, projectId: 'Website', name, order, kind });

/** `GET /deploy/status`, as `EnvironmentStatusProjection` derives it from the runs above. */
export function deployStatusFixture(): EnvironmentStatus[] {
  const runs = deployDeploymentsFixture();
  return [
    { environment: environment('stage', 'Staging', 0, 'staging'), currentRelease: RELEASE_3,
      currentDeployment: runs.find(item => item.id === 'run-c-stage-redeploy')!, newerRelease: null, cardsSinceLastDeploy: [], health: 'healthy' },
    { environment: environment('production', 'Production', 1, 'production'), currentRelease: RELEASE_1,
      currentDeployment: runs.find(item => item.id === 'run-c-production')!, newerRelease: RELEASE_3,
      cardsSinceLastDeploy: ['WEB-33', 'WEB-34', 'WEB-35'], health: 'healthy' },
  ];
}

/** Every source ready: AGT-3176 environments plus the AGT-3201 deploy routes. */
export function deploySourcesFixture(): DeploySources {
  return {
    environments: deployEnvironmentsFixture(), statuses: deployStatusFixture(),
    deployments: deployDeploymentsFixture(), releases: deployReleasesFixture(),
  };
}

/** A server without AGT-3201: only the AGT-3176 environments answer. */
export function hostOnlySourcesFixture(): DeploySources {
  return { environments: deployEnvironmentsFixture(), statuses: null, deployments: null, releases: null };
}

export function deployRunsFixture(): DeployRun[] {
  const sources = deploySourcesFixture();
  return buildDeployRuns(sources.deployments!, environmentNames(sources), sources.releases);
}

const LOGS: Record<string, DeploymentLogLine[]> = {
  'run-b-stage': [
    { step: 'publish-static', atUtc: '2026-10-19T07:58:00Z', message: 'Release 2026.10.19.1 queued for Staging by release-gate (automatic after integration).' },
    { step: 'publish-static', atUtc: '2026-10-19T07:59:00Z', message: 'Claimed by deploy host web-vm.' },
    { step: 'health', atUtc: '2026-10-19T08:00:00Z', message: 'Health check failed (health-http-failed).' },
  ],
  'run-c-production': [
    { step: 'approval', atUtc: '2026-10-19T11:49:00Z', message: 'Release 2026.10.19.2 awaits Owner or Maintainer approval for Production.' },
  ],
};

/** `GET /deploy/deployments/{id}/log` lines. */
export function deploymentLogFixture(deploymentId: string): DeploymentLogLine[] {
  return LOGS[deploymentId] ?? [];
}

/** AGT-3221 `GET /pipeline-definition` with its static-site and container steps. */
export function pipelineDefinitionFixture(): PipelineDefinitionResponse {
  const step = (kind: string, artifact: string, target: string, destination: string, path: string) => ({
    path, kind, artifact, target, destination, healthUrl: '/healthz', healthTimeoutSeconds: 60,
  });
  const steps = [step('static-site', 'web', 'web-vm', '/srv/www/website', 'deploy[0]'), step('container', 'api', 'app-vm', '/srv/api/compose.yml (api)', 'deploy[1]')];
  return {
    project: 'Website', path: '.agent-studio/project.yml', exists: true, schemaVersion: 2,
    definitionSha256: 'test', migrationAvailable: false,
    valid: true,
    issues: [],
    diagram: {
      schemaVersion: 2, environmentsDefaulted: false, lifecycleDefaulted: false,
      gate: { source: 'pipeline', build: ['npm run build'], test: ['npm test'], testSuites: [] },
      artifacts: [
        { path: 'artifacts.web', name: 'web', type: 'static', source: 'dist', image: null },
        { path: 'artifacts.api', name: 'api', type: 'container', source: 'Dockerfile', image: 'registry.example/website-api' },
      ],
      environments: [
        { path: 'environments.staging', name: 'staging', order: 0, protected: false, automatic: true, approval: [], requires: null, steps, variables: [] },
        { path: 'environments.production', name: 'production', order: 1, protected: true, automatic: false, approval: ['Owner', 'Maintainer'], requires: 'staging', steps, variables: [] },
      ],
    },
  };
}
