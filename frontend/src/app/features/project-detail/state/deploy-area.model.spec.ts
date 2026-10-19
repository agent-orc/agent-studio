import {
  ALL_DEPLOY_RUNS,
  DEPLOYMENT_STATES,
  RUN_STATE_LABEL,
  STEP_STATE_LABEL,
  buildDeployRuns,
  buildEnvironmentCards,
  environmentNames,
  filterDeployRuns,
  formatAge,
  formatDuration,
  groupStepLog,
  releaseForArtifact,
  runActions,
  runState,
  shortArtifact,
  stepState,
  triggerLabel,
  type DeploySources,
} from './deploy-area.model';
import {
  ARTIFACT_B,
  ARTIFACT_C,
  DEPLOY_FIXTURE_NOW,
  RELEASE_3,
  deployDeploymentsFixture,
  deployEnvironmentsFixture,
  deployReleasesFixture,
  deployRunsFixture,
  deploySourcesFixture,
  deployStatusFixture,
  deploymentLogFixture,
  hostOnlySourcesFixture,
} from '../testing/deploy-fixtures';

/**
 * Copied from AGT-3201 backend/Features/Deploy/DeployContract.cs. A new server
 * state belongs in these lists; the mapping tests then fail until the UI maps it.
 */
const SERVER_DEPLOYMENT_STATES = ['queued', 'running', 'succeeded', 'failed', 'cancelled', 'awaiting-approval'];
const SERVER_STEP_ONLY_STATES = ['pending', 'skipped'];
const SERVER_TRIGGERS = ['deploy', 'redeploy', 'rollback', 'automatic-rollback'];

describe('deploy area model', () => {
  const now = DEPLOY_FIXTURE_NOW;

  it('shortens an artifact to its name and the first digest characters', () => {
    expect(shortArtifact(ARTIFACT_B)).toBe('website@sha256:bbbbbbbbbbbb');
    expect(shortArtifact('plain-name')).toBe('plain-name');
  });

  it('formats ages and durations against the injected time', () => {
    expect(formatAge(null, now)).toBeNull();
    expect(formatAge('2026-10-19T11:59:40Z', now)).toBe('just now');
    expect(formatAge('2026-10-19T11:48:00Z', now)).toBe('12 min');
    expect(formatAge('2026-10-19T09:00:00Z', now)).toBe('3 h');
    expect(formatAge('2026-10-16T12:00:00Z', now)).toBe('3 d');
    expect(formatDuration('2026-10-19T11:45:00Z', '2026-10-19T11:48:05Z')).toBe('3 min 5 s');
    expect(formatDuration('2026-10-19T11:45:00Z', null)).toBeNull();
  });

  it('matches a release by digest regardless of the registry prefix', () => {
    expect(releaseForArtifact(ARTIFACT_C, deployReleasesFixture())?.version).toBe('2026.10.19.2');
    expect(releaseForArtifact(ARTIFACT_C, null)).toBeNull();
  });

  describe('state mapping', () => {
    it('maps every server deployment state to itself with its own label', () => {
      expect([...DEPLOYMENT_STATES]).toEqual(SERVER_DEPLOYMENT_STATES);
      for (const state of SERVER_DEPLOYMENT_STATES) {
        expect(runState(state)).toBe(state);
        expect(RUN_STATE_LABEL[runState(state)]).not.toBe(RUN_STATE_LABEL.unknown);
      }
      const labels = SERVER_DEPLOYMENT_STATES.map(state => RUN_STATE_LABEL[runState(state)]);
      expect(new Set(labels).size).toBe(labels.length);
    });

    it('keeps cancelled as cancelled, not queued', () => {
      expect(runState('cancelled')).toBe('cancelled');
      expect(RUN_STATE_LABEL[runState('cancelled')]).toBe('Cancelled');
    });

    it('maps every server step state, including pending and skipped', () => {
      for (const state of [...SERVER_DEPLOYMENT_STATES, ...SERVER_STEP_ONLY_STATES]) {
        expect(stepState(state)).toBe(state);
        expect(STEP_STATE_LABEL[stepState(state)]).not.toBe(STEP_STATE_LABEL.unknown);
      }
    });

    it('shows a state the UI does not know as unknown instead of borrowing another meaning', () => {
      expect(runState('paused')).toBe('unknown');
      expect(stepState('paused')).toBe('unknown');
      expect(RUN_STATE_LABEL.unknown).toBe('Unknown state');
    });

    it('labels every server trigger and keeps an unknown trigger readable', () => {
      for (const trigger of SERVER_TRIGGERS) expect(triggerLabel(trigger)).not.toBe(trigger);
      expect(triggerLabel('automatic-rollback')).toBe('Automatic rollback');
      expect(triggerLabel('hotfix')).toBe('hotfix');
    });

    it('offers approval only while awaiting it and cancellation only before a host claims the run', () => {
      const actions = Object.fromEntries([...DEPLOYMENT_STATES, 'unknown' as const].map(state => [state, runActions(state)]));
      expect(actions).toEqual({
        queued: ['cancel'], running: [], succeeded: [], failed: [], cancelled: [],
        'awaiting-approval': ['approve', 'reject', 'cancel'], unknown: [],
      });
    });
  });

  describe('buildDeployRuns', () => {
    it('lists AGT-3201 deployments newest first with environment names and release versions', () => {
      const runs = deployRunsFixture();

      expect(runs.map(run => run.id)).toEqual([
        'run-c-stage-redeploy', 'run-c-production', 'run-c-stage', 'run-a-stage-rollback', 'run-b-stage', 'run-a-production', 'run-a-stage',
      ]);
      expect(runs[1]).toMatchObject({
        environmentName: 'Production', releaseVersion: '2026.10.19.2', trigger: 'deploy', state: 'awaiting-approval',
        requestedBy: 'local.dev', approvedBy: null, startedAt: null,
      });
      expect(runs[1].steps.map(step => [step.name, step.state])).toEqual([
        ['approval', 'awaiting-approval'], ['publish-static', 'pending'], ['health', 'pending'],
      ]);
      expect(runs.find(run => run.id === 'run-a-production')?.approvedBy).toBe('local.owner');
      expect(runs[0].state).toBe('cancelled');
    });

    it('keeps a run whose release or state the UI does not know', () => {
      const [first] = deployDeploymentsFixture();
      const runs = buildDeployRuns([{ ...first, state: 'paused', releaseId: 'gone' }], new Map(), null);

      expect(runs[0]).toMatchObject({ state: 'unknown', releaseVersion: null, environmentName: 'stage' });
    });
  });

  describe('buildEnvironmentCards', () => {
    it('reads release, deploy time, cards, health, last run, and approval from AGT-3201', () => {
      const [staging, production] = buildEnvironmentCards(deploySourcesFixture());

      expect(staging).toEqual({
        id: 'stage', name: 'Staging', address: 'https://stage.website.example', artifact: ARTIFACT_C,
        releaseVersion: '2026.10.19.2', deployedAt: '2026-10-19T11:48:00Z', cards: { status: 'known', keys: ['WEB-35'] },
        newer: null, health: { status: 'reported', state: 'healthy' },
        lastRun: { deploymentId: 'run-c-stage-redeploy', trigger: 'redeploy', state: 'cancelled', at: '2026-10-19T11:55:00Z' },
        awaitingApproval: null,
      });
      expect(production).toMatchObject({
        releaseVersion: '2026.10.18.1', deployedAt: '2026-10-18T22:10:00Z', cards: { status: 'known', keys: ['WEB-29', 'WEB-30'] },
        newer: { version: '2026.10.19.2', cardKeys: ['WEB-33', 'WEB-34', 'WEB-35'] },
        lastRun: { deploymentId: 'run-c-production', trigger: 'deploy', state: 'awaiting-approval', at: '2026-10-19T11:49:00Z' },
        awaitingApproval: { deploymentId: 'run-c-production', subject: 'Release 2026.10.19.2', requestedBy: 'local.dev' },
      });
    });

    it('falls back to the AGT-3176 environment state without the deploy service', () => {
      const [staging, production] = buildEnvironmentCards(hostOnlySourcesFixture());

      expect(staging).toMatchObject({
        artifact: ARTIFACT_C, releaseVersion: null, deployedAt: '2026-10-19T11:48:00Z',
        cards: { status: 'unknown', reason: 'Card lists arrive with release records' },
        health: { status: 'reported', state: 'healthy' },
        lastRun: { deploymentId: null, trigger: 'deploy', state: 'succeeded', at: '2026-10-19T11:48:00Z' },
        awaitingApproval: null,
      });
      expect(production.awaitingApproval).toEqual({ deploymentId: null, subject: 'website@sha256:cccccccccccc', requestedBy: null });
    });

    it('prefers AGT-3201 health over the host report and maps the host "failed" to unhealthy', () => {
      const sources = deploySourcesFixture();
      sources.statuses![0].health = 'unhealthy';
      expect(buildEnvironmentCards(sources)[0].health).toEqual({ status: 'reported', state: 'unhealthy' });

      const host = hostOnlySourcesFixture();
      host.environments!.environments[0].health = 'failed';
      expect(buildEnvironmentCards(host)[0].health).toEqual({ status: 'reported', state: 'unhealthy' });
    });

    it('reads "no checks" only for an unknown health without a declared health URL', () => {
      const host = hostOnlySourcesFixture();
      host.environments!.environments[0].health = 'unknown';
      expect(buildEnvironmentCards(host)[0].health).toEqual({ status: 'reported', state: 'unknown' });

      host.environments!.environments[0].declaration.healthUrl = null;
      expect(buildEnvironmentCards(host)[0].health).toEqual({ status: 'no-checks' });
    });

    it('shows an approval requested through AGT-3201 even without a pending AGT-3176 promotion', () => {
      const sources = deploySourcesFixture();
      sources.environments!.promotions = [];
      sources.releases = [];

      expect(buildEnvironmentCards(sources)[1].awaitingApproval)
        .toEqual({ deploymentId: 'run-c-production', subject: `Release ${RELEASE_3.version}`, requestedBy: 'local.dev' });
    });

    it('builds the cards from AGT-3201 status when the environments read fails', () => {
      const sources: DeploySources = { ...deploySourcesFixture(), environments: null };
      const cards = buildEnvironmentCards(sources);

      expect(cards.map(card => [card.id, card.name, card.address])).toEqual([['stage', 'Staging', null], ['production', 'Production', null]]);
      expect(cards[0].releaseVersion).toBe('2026.10.19.2');
    });

    it('says "nothing deployed" for an environment without history or runs', () => {
      const environments = deployEnvironmentsFixture();
      environments.environments[1] = { ...environments.environments[1], deployedArtifact: null, history: [] };
      const [, production] = buildEnvironmentCards({ environments, statuses: null, deployments: [], releases: [] });

      expect(production).toMatchObject({ artifact: null, deployedAt: null, lastRun: null,
        cards: { status: 'unknown', reason: 'Nothing deployed yet' } });
    });
  });

  describe('filterDeployRuns', () => {
    const runs = deployRunsFixture();

    it('returns every run without filters', () => {
      expect(filterDeployRuns(runs, ALL_DEPLOY_RUNS, now)).toHaveLength(7);
    });

    it('filters by environment, state, and period, and combines them', () => {
      expect(filterDeployRuns(runs, { ...ALL_DEPLOY_RUNS, environmentId: 'production' }, now).map(run => run.id))
        .toEqual(['run-c-production', 'run-a-production']);
      expect(filterDeployRuns(runs, { ...ALL_DEPLOY_RUNS, state: 'cancelled' }, now).map(run => run.id)).toEqual(['run-c-stage-redeploy']);
      expect(filterDeployRuns(runs, { ...ALL_DEPLOY_RUNS, period: '24h' }, now)).toHaveLength(6);
      expect(filterDeployRuns(runs, { environmentId: 'stage', state: 'succeeded', period: '24h' }, now).map(run => run.id))
        .toEqual(['run-c-stage', 'run-a-stage-rollback']);
    });

    it('measures the period from the injected time, not the wall clock', () => {
      expect(filterDeployRuns(runs, { ...ALL_DEPLOY_RUNS, period: '24h' }, Date.parse('2026-10-25T00:00:00Z'))).toHaveLength(0);
    });
  });

  it('names environments from the AGT-3176 declarations, then AGT-3201 status', () => {
    const names = environmentNames({ environments: null, statuses: deployStatusFixture() });
    expect([...names]).toEqual([['stage', 'Staging'], ['production', 'Production']]);
  });

  it('groups log lines under their steps and keeps lines of an unlisted step', () => {
    const run = deployRunsFixture().find(item => item.id === 'run-b-stage')!;
    const lines = [...deploymentLogFixture('run-b-stage'), { step: 'rollback', atUtc: '2026-10-19T08:00:30Z', message: 'Automatic rollback queued.' }];
    const groups = groupStepLog(run.steps, lines);

    expect(groups.map(group => [group.step, group.state, group.lines.length])).toEqual([
      ['publish-static', 'succeeded', 2], ['health', 'failed', 1], ['rollback', null, 1],
    ]);
  });
});
