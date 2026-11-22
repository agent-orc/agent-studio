import type { StatusTone } from '../../../components/status-indicator/status-indicator.component';

/**
 * Wire shapes of `GET /api/projects/{project}/deploy/instance` (AGT-3879):
 * the running Studio instance as the Production environment of its source
 * project, the cards its live release brought, the cards integrated after the
 * live commit, and the latest deploy run the operator scripts reported.
 */
export type InstanceRunState = 'running' | 'succeeded' | 'failed' | 'stalled';

export interface InstanceDeployStep {
  name: string;
  kind: string;
  state: string;
  startedAtUtc: string | null;
  finishedAtUtc: string | null;
  message: string | null;
  reportedAtUtc: string;
}

export interface InstanceDeployRun {
  id: string;
  releaseId: string;
  version: string;
  targetCommit: string;
  state: InstanceRunState | string;
  startedAtUtc: string;
  finishedAtUtc: string | null;
  lastReportAtUtc: string;
  lastStep: string | null;
  failedStep: string | null;
  components: Record<string, string>;
  steps: InstanceDeployStep[];
  fromVersionRoute: boolean;
  message: string | null;
}

export interface InstanceCard { key: string; title: string | null; landedCommit: string | null }

export interface InstanceRelease {
  id: string;
  version: string;
  commitSha: string;
  liveSinceUtc: string | null;
  cards: InstanceCard[];
  cardsResolved: boolean;
  fromVersionRoute: boolean;
  components: Record<string, string>;
}

export interface InstanceDeployView {
  projectId: string;
  environmentId: string;
  environmentName: string;
  live: InstanceRelease | null;
  latestRun: InstanceDeployRun | null;
  notLiveYet: InstanceCard[];
  developRef: string | null;
  developCommit: string | null;
  factsComputedAtUtc: string | null;
  stalledAfterMinutes: number;
}

export function shortCommit(sha: string | null | undefined): string {
  return sha ? sha.slice(0, 9) : '';
}

/** One time format for the panel: the UTC minute, "2026-11-16 03:35 UTC". */
export function utcMinute(iso: string | null | undefined): string {
  if (!iso) return '';
  const at = new Date(iso);
  return Number.isNaN(at.getTime()) ? iso : `${at.toISOString().slice(0, 16).replace('T', ' ')} UTC`;
}

export function instanceRunTone(state: string): StatusTone {
  switch (state) {
    case 'succeeded': return 'success';
    case 'failed': return 'danger';
    case 'stalled': return 'warning';
    case 'running': return 'running';
    default: return 'neutral';
  }
}

export function instanceStepTone(state: string): StatusTone {
  return state === 'skipped' ? 'neutral' : instanceRunTone(state);
}

/** The run in one line, naming the step a failed or stalled run stopped at. */
export function instanceRunLabel(run: InstanceDeployRun, stalledAfterMinutes: number): string {
  const step = run.failedStep ?? run.lastStep;
  switch (run.state) {
    case 'running': return step ? `Deploy running · ${step}` : 'Deploy running';
    case 'succeeded': return run.fromVersionRoute ? 'Recorded from the running version' : 'Last deploy succeeded';
    case 'failed': return step ? `Deploy failed at ${step}` : 'Deploy failed';
    case 'stalled': return `Deploy stalled${run.lastStep ? ` after ${run.lastStep}` : ''} · no report for ${stalledAfterMinutes} min`;
    default: return run.state;
  }
}
