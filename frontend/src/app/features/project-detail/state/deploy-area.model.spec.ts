import {
  ALL_DEPLOY_RUNS,
  buildDeployRuns,
  buildEnvironmentCards,
  filterDeployRuns,
  formatAge,
  formatDuration,
  releaseForArtifact,
  shortArtifact,
} from './deploy-area.model';
import {
  ARTIFACT_A,
  ARTIFACT_B,
  ARTIFACT_C,
  DEPLOY_FIXTURE_NOW,
  deployEnvironmentsFixture,
  deployHealthFixture,
  deployReleasesFixture,
} from '../testing/deploy-fixtures';

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
    expect(releaseForArtifact(ARTIFACT_B, deployReleasesFixture())?.version).toBe('2026.10.19.1');
    expect(releaseForArtifact(ARTIFACT_C, deployReleasesFixture())).toBeNull();
    expect(releaseForArtifact(ARTIFACT_B, null)).toBeNull();
  });

  describe('buildDeployRuns', () => {
    it('lists one run per job, newest first, with the finish time and evidence of its report', () => {
      const runs = buildDeployRuns(deployEnvironmentsFixture(), deployReleasesFixture());

      expect(runs.map(run => run.id)).toEqual([
        'job-b-stage', 'job-a-stage-rollback', 'job-c-stage', 'job-a-production', 'job-a-stage',
      ]);
      const failed = runs.find(run => run.id === 'job-c-stage')!;
      expect(failed.outcome).toBe('failed');
      expect(failed.finishedAt).toBe('2026-10-19T08:00:00Z');
      expect(failed.evidence).toBe('health-http-failed');
      expect(failed.releaseVersion).toBeNull();
      const rollback = runs.find(run => run.id === 'job-a-stage-rollback')!;
      expect(rollback.kind).toBe('rollback');
      expect(rollback.finishedAt).toBe('2026-10-19T08:02:00Z');
      expect(rollback.releaseVersion).toBe('2026.10.18.1');
    });

    it('derives a step log from the recorded job events', () => {
      const run = buildDeployRuns(deployEnvironmentsFixture(), null).find(item => item.id === 'job-c-stage')!;

      expect(run.steps.map(step => [step.name, step.state, step.detail])).toEqual([
        ['Queued', 'succeeded', 'Requested by release-gate'],
        ['Claimed by deploy host', 'succeeded', 'web-vm'],
        ['Deploy and check health', 'failed', 'Health check failed'],
      ]);
    });

    it('maps queued and claimed jobs to queued and running runs without a finish', () => {
      const data = deployEnvironmentsFixture();
      data.jobs = [
        { ...data.jobs![0], id: 'waiting', status: 'queued', claimedAt: null, createdAt: '2026-10-19T11:58:00Z' },
        { ...data.jobs![0], id: 'claimed', status: 'claimed', createdAt: '2026-10-19T11:59:00Z' },
      ];
      const runs = buildDeployRuns(data, null).filter(run => !run.id.startsWith('history:'));

      expect(runs.map(run => [run.id, run.outcome, run.finishedAt])).toEqual([
        ['claimed', 'running', null],
        ['waiting', 'queued', null],
      ]);
      expect(runs[1].steps[1].state).toBe('queued');
    });

    it('keeps history rows that no job reported, so older state files stay visible', () => {
      const data = deployEnvironmentsFixture();
      data.jobs = undefined;
      const runs = buildDeployRuns(data, null);

      expect(runs).toHaveLength(5);
      expect(runs[0]).toMatchObject({ id: 'history:stage:2026-10-19T11:48:00Z', outcome: 'succeeded', kind: 'deploy' });
      expect(runs.find(run => run.queuedAt === '2026-10-19T08:02:00Z')?.kind).toBe('rollback');
    });
  });

  describe('buildEnvironmentCards', () => {
    const cards = (releases = deployReleasesFixture(), health = deployHealthFixture()) => {
      const data = deployEnvironmentsFixture();
      return buildEnvironmentCards(data, buildDeployRuns(data, releases), releases, health);
    };

    it('shows the live release with its cards, age source, and last run per environment', () => {
      const [staging, production] = cards();

      expect(staging).toMatchObject({
        id: 'stage', releaseVersion: '2026.10.19.1', deployedAt: '2026-10-19T11:48:00Z',
        cards: { status: 'known', keys: ['WEB-33', 'WEB-34', 'WEB-35'] }, awaitingApproval: null,
      });
      expect(staging.lastRun?.id).toBe('job-b-stage');
      expect(production).toMatchObject({ releaseVersion: '2026.10.18.1', awaitingApproval: ARTIFACT_B });
      expect(production.lastRun?.id).toBe('job-a-production');
    });

    it('reports health only where checks exist and "no checks" elsewhere', () => {
      const [staging, production] = cards();

      expect(staging.health).toEqual({ status: 'checks', state: 'healthy', checkCount: 2, checkedAt: '2026-10-19T11:59:00Z' });
      expect(production.health).toEqual({ status: 'no-checks' });
      expect(cards(deployReleasesFixture(), null as never).every(card => card.health.status === 'no-checks')).toBe(true);
    });

    it('explains why the card list is unknown without release records', () => {
      expect(cards(null as never)[0].cards).toEqual({ status: 'unknown', reason: 'Card lists arrive with release records' });
      expect(cards([])[0].cards).toEqual({ status: 'unknown', reason: 'No release record for this artifact' });
    });

    it('skips a failed latest deploy when naming what runs now', () => {
      const data = deployEnvironmentsFixture();
      data.environments[0].history.unshift(
        { at: '2026-10-19T11:55:00Z', status: 'failed', artifact: ARTIFACT_C, previousArtifact: ARTIFACT_B, evidence: 'deploy-step-failed' });
      const [staging] = buildEnvironmentCards(data, [], null, null);

      expect(staging.artifact).toBe(ARTIFACT_B);
      expect(staging.deployedAt).toBe('2026-10-19T11:48:00Z');
    });

    it('reads "nothing deployed" for an environment without history', () => {
      const data = deployEnvironmentsFixture();
      data.environments[1].history = [];
      const production = buildEnvironmentCards(data, [], null, null)[1];

      expect(production).toMatchObject({ artifact: null, deployedAt: null, lastRun: null });
      expect(production.cards).toEqual({ status: 'unknown', reason: 'Nothing deployed yet' });
    });
  });

  describe('filterDeployRuns', () => {
    const runs = buildDeployRuns(deployEnvironmentsFixture(), deployReleasesFixture());

    it('returns every run without filters', () => {
      expect(filterDeployRuns(runs, ALL_DEPLOY_RUNS, now)).toHaveLength(5);
    });

    it('filters by environment, outcome, and period, and combines them', () => {
      expect(filterDeployRuns(runs, { ...ALL_DEPLOY_RUNS, environmentId: 'production' }, now).map(run => run.id))
        .toEqual(['job-a-production']);
      expect(filterDeployRuns(runs, { ...ALL_DEPLOY_RUNS, outcome: 'failed' }, now).map(run => run.id))
        .toEqual(['job-c-stage']);
      expect(filterDeployRuns(runs, { ...ALL_DEPLOY_RUNS, period: '24h' }, now).map(run => run.id))
        .toEqual(['job-b-stage', 'job-a-stage-rollback', 'job-c-stage', 'job-a-production']);
      expect(filterDeployRuns(runs, { environmentId: 'production', outcome: 'failed', period: '7d' }, now)).toEqual([]);
    });

    it('measures the period from the injected time, not the wall clock', () => {
      const muchLater = now + 30 * 86_400_000;
      expect(filterDeployRuns(runs, { ...ALL_DEPLOY_RUNS, period: '7d' }, muchLater)).toEqual([]);
      expect(filterDeployRuns(runs, { ...ALL_DEPLOY_RUNS, period: '24h' }, now).map(run => run.artifact)).toContain(ARTIFACT_A);
    });
  });
});
