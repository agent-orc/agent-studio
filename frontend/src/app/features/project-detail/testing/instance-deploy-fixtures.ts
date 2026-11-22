import type { InstanceDeployRun, InstanceDeployView } from '../state/instance-deploy.model';

/** Pinned "now" of the instance deploy fixtures: 16 November 2026, 09:00 UTC. */
export const INSTANCE_FIXTURE_NOW = Date.parse('2026-11-16T09:00:00Z');

const COMMIT = '5faca674a1b2c3d4e5f60718293a4b5c6d7e8f90';
const NEXT = '7b1d0e2c9a8f7e6d5c4b3a291807f6e5d4c3b2a1';

function steps(state: 'succeeded' | 'failed' | 'stalled' | 'running'): InstanceDeployRun['steps'] {
  const done = (name: string, kind: string, at: string) => ({ name, kind, state: 'succeeded', startedAtUtc: at,
    finishedAtUtc: at, message: null, reportedAtUtc: at });
  const base = [
    done('build:server', 'build', '2026-11-16T03:10:00Z'), done('build:engine', 'build', '2026-11-16T03:14:00Z'),
    done('build:runner', 'build', '2026-11-16T03:18:00Z'), done('build:gate-worker', 'build', '2026-11-16T03:22:00Z'),
    done('build:review-host', 'build', '2026-11-16T03:26:00Z'), done('switch:server', 'server-switch', '2026-11-16T03:35:00Z'),
  ];
  if (state === 'succeeded') return [...base, done('host:runner-1', 'host', '2026-11-16T03:38:00Z'),
    done('review-host:review-1', 'review-host', '2026-11-16T03:40:00Z')];
  if (state === 'failed') return [...base, { name: 'host:runner-1', kind: 'host', state: 'failed',
    startedAtUtc: '2026-11-16T03:36:00Z', finishedAtUtc: '2026-11-16T03:39:00Z',
    message: 'Runner package install exited 1.', reportedAtUtc: '2026-11-16T03:39:00Z' }];
  return [...base, { name: 'host:runner-1', kind: 'host', state: 'running', startedAtUtc: '2026-11-16T03:36:00Z',
    finishedAtUtc: null, message: null, reportedAtUtc: '2026-11-16T03:36:00Z' }];
}

/** The Studio's own project after a deploy: live release, its cards, and what the next deploy brings. */
export function instanceDeployFixture(run: 'succeeded' | 'failed' | 'stalled' | 'running' = 'succeeded'): InstanceDeployView {
  const runSteps = steps(run);
  const last = runSteps.at(-1)!;
  return {
    projectId: 'Agent Studio', environmentId: 'studio-instance', environmentName: 'Production',
    live: {
      id: 'instance-0123456789abcdef', version: '0.9.5-5faca674a', commitSha: COMMIT,
      liveSinceUtc: '2026-11-16T03:35:00Z', cardsResolved: true, fromVersionRoute: false,
      components: { server: '0.9.5-5faca674a', engine: '0.9.5-5faca674a', runner: '0.9.5-5faca674a',
        'gate-worker': '0.9.5-5faca674a', 'review-host': '0.9.5-5faca674a' },
      cards: [
        { key: 'AGT-3794', title: 'Usage panel with one time format', landedCommit: COMMIT },
        { key: 'AGT-3839', title: 'Make the concept-remote journey green', landedCommit: COMMIT },
      ],
    },
    latestRun: {
      id: '20261116T0310Z-5faca674a', releaseId: 'instance-0123456789abcdef', version: '0.9.5-5faca674a',
      targetCommit: COMMIT, state: run, startedAtUtc: '2026-11-16T03:10:00Z',
      finishedAtUtc: run === 'succeeded' ? '2026-11-16T03:40:00Z' : null,
      lastReportAtUtc: last.reportedAtUtc, lastStep: last.name, failedStep: run === 'failed' ? last.name : null,
      components: {}, steps: runSteps, fromVersionRoute: false, message: null,
    },
    notLiveYet: [
      { key: 'AGT-3861', title: 'Hosted data panel keeps its scroll position', landedCommit: NEXT },
      { key: 'AGT-3870', title: 'Gate slots show the waiting reason', landedCommit: NEXT },
      { key: 'AGT-3879', title: 'Show the Studio deployment in the Studio', landedCommit: NEXT },
    ],
    developRef: 'origin/develop', developCommit: NEXT, factsComputedAtUtc: '2026-11-16T08:58:00Z',
    stalledAfterMinutes: 30,
  };
}
