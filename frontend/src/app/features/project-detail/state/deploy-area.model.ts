import { InjectionToken } from '@angular/core';
import type { Observable } from 'rxjs';
import type { ProjectEnvironmentHistory, ProjectEnvironmentJob, ProjectEnvironments } from '../../../models/project-overview.model';

/** Release from the shared P1 data contract (BD-1, served by AGT-3201). */
export interface Release {
  id: string;
  projectId: string;
  version: string;
  commitSha: string;
  artifactDigests: string[];
  processVersion: string;
  variablesVersion: string;
  cardKeys: string[];
  createdAtUtc: string;
  channel: string;
}

export type DeploymentState = 'queued' | 'running' | 'succeeded' | 'failed' | 'cancelled' | 'awaiting-approval';

/**
 * One panel's data. `unavailable` means the source does not exist on this
 * server yet (for example releases before AGT-3201); it is not an error.
 */
export type DeployPanel<T> =
  | { status: 'loading' }
  | { status: 'ready'; value: T }
  | { status: 'unavailable' }
  | { status: 'error'; message: string };

/** Latest health-check result per environment (AGT-3223). */
export interface DeployHealthCheckSummary {
  environmentId: string;
  state: 'healthy' | 'unhealthy' | 'unknown';
  checkCount: number;
  checkedAtUtc: string | null;
}

export interface DeployHealthCheckSource {
  summaries(projectName: string): Observable<DeployHealthCheckSummary[]>;
}

/** Health checks connect here; without a source every card reads "No checks". */
export const DEPLOY_HEALTH_CHECKS = new InjectionToken<DeployHealthCheckSource | null>('DEPLOY_HEALTH_CHECKS', {
  providedIn: 'root',
  factory: () => null,
});

/** Epoch-millisecond clock for ages and the history period filter; tests replace it. */
export const DEPLOY_CLOCK = new InjectionToken<() => number>('DEPLOY_CLOCK', {
  providedIn: 'root',
  factory: () => () => Date.now(),
});

export interface DeployLifecycleNode {
  id: string;
  kind: 'gate' | 'artifact' | 'environment';
  name: string;
  items: string[];
  automatic: boolean;
  approval: string[];
  requires: string | null;
}

/** The `GET /api/projects/{p}/execution` fields the Pipeline view reads. */
export interface DeployLifecycleRead {
  valid: boolean;
  lifecycle?: { declared: boolean; nodes: DeployLifecycleNode[] } | null;
}

export interface DeployRunStep {
  name: string;
  state: DeploymentState;
  at: string | null;
  detail: string | null;
}

export interface DeployRun {
  id: string;
  environmentId: string;
  environmentName: string;
  artifact: string;
  releaseVersion: string | null;
  kind: 'deploy' | 'rollback';
  outcome: DeploymentState;
  requestedBy: string | null;
  hostId: string | null;
  queuedAt: string;
  finishedAt: string | null;
  evidence: string | null;
  steps: DeployRunStep[];
}

export type DeployCards = { status: 'known'; keys: string[] } | { status: 'unknown'; reason: string };
export type DeployHealth =
  | { status: 'checks'; state: DeployHealthCheckSummary['state']; checkCount: number; checkedAt: string | null }
  | { status: 'no-checks' };

export interface EnvironmentCardView {
  id: string;
  name: string;
  kind: 'stage' | 'production' | 'other';
  address: string;
  artifact: string | null;
  releaseVersion: string | null;
  deployedAt: string | null;
  cards: DeployCards;
  health: DeployHealth;
  lastRun: DeployRun | null;
  awaitingApproval: string | null;
}

export type DeployPeriod = '24h' | '7d' | '30d' | 'all';
export interface DeployRunFilter {
  environmentId: string;
  outcome: DeploymentState | 'all';
  period: DeployPeriod;
}
export const ALL_DEPLOY_RUNS: DeployRunFilter = { environmentId: 'all', outcome: 'all', period: 'all' };

const PERIOD_MS: Record<DeployPeriod, number> = {
  '24h': 86_400_000,
  '7d': 7 * 86_400_000,
  '30d': 30 * 86_400_000,
  all: Number.POSITIVE_INFINITY,
};

const JOB_OUTCOME: Record<string, DeploymentState> = {
  queued: 'queued',
  claimed: 'running',
  healthy: 'succeeded',
  failed: 'failed',
};

const EVIDENCE_LABEL: Record<string, string> = {
  'health-ok': 'Health check passed',
  'health-http-failed': 'Health check failed',
  'deploy-step-failed': 'Deploy step failed',
};

export const OUTCOME_LABEL: Record<DeploymentState, string> = {
  queued: 'Queued',
  running: 'Running',
  succeeded: 'Succeeded',
  failed: 'Failed',
  cancelled: 'Cancelled',
  'awaiting-approval': 'Awaiting approval',
};

export function evidenceLabel(evidence: string | null): string | null {
  return evidence ? EVIDENCE_LABEL[evidence] ?? evidence : null;
}

/** `registry/app@sha256:<hex>` reads as `app@sha256:<first 12 hex>`. */
export function shortArtifact(artifact: string): string {
  const at = artifact.lastIndexOf('@');
  const name = (at >= 0 ? artifact.slice(0, at) : artifact).split('/').pop() ?? artifact;
  if (at < 0) return name;
  const digest = artifact.slice(at + 1);
  const colon = digest.indexOf(':');
  return `${name}@${digest.slice(0, colon + 1)}${digest.slice(colon + 1, colon + 13)}`;
}

/** Compact relative age: "just now", "12 min", "3 h", "4 d". */
export function formatAge(iso: string | null, now: number): string | null {
  if (!iso) return null;
  const minutes = Math.floor(Math.max(0, now - Date.parse(iso)) / 60_000);
  if (minutes < 1) return 'just now';
  if (minutes < 60) return `${minutes} min`;
  const hours = Math.floor(minutes / 60);
  return hours < 24 ? `${hours} h` : `${Math.floor(hours / 24)} d`;
}

export function formatDuration(fromIso: string | null, toIso: string | null): string | null {
  if (!fromIso || !toIso) return null;
  const seconds = Math.max(0, Math.round((Date.parse(toIso) - Date.parse(fromIso)) / 1000));
  return seconds < 60 ? `${seconds} s` : `${Math.floor(seconds / 60)} min ${seconds % 60} s`;
}

function digestOf(artifact: string): string {
  const at = artifact.lastIndexOf('@');
  return at >= 0 ? artifact.slice(at + 1) : artifact;
}

export function releaseForArtifact(artifact: string | null, releases: readonly Release[] | null): Release | null {
  if (!artifact || !releases) return null;
  const digest = digestOf(artifact);
  return releases.find(release => release.artifactDigests.some(item => digestOf(item) === digest)) ?? null;
}

const SUCCESS_HISTORY = new Set(['healthy', 'rolled-back']);

/**
 * Deploy runs from the AGT-3176 job queue, newest first. A finished job takes
 * its finish time and evidence from the environment history row it reported;
 * history rows without a job (older state files) still appear as runs.
 */
export function buildDeployRuns(data: ProjectEnvironments, releases: readonly Release[] | null): DeployRun[] {
  const names = new Map(data.environments.map(item => [item.declaration.id, item.declaration.name]));
  const unmatched = new Map(data.environments.map(item => [item.declaration.id,
    [...item.history].sort((a, b) => Date.parse(a.at) - Date.parse(b.at))]));
  const jobs = [...(data.jobs ?? [])].sort((a, b) => Date.parse(a.createdAt) - Date.parse(b.createdAt));
  const runs = jobs.map(job => jobRun(job, names, takeReport(job, unmatched), releases));
  for (const [environmentId, rows] of unmatched) {
    for (const row of rows) runs.push(historyRun(environmentId, names, row, releases));
  }
  return runs.sort((a, b) => Date.parse(b.queuedAt) - Date.parse(a.queuedAt) || b.id.localeCompare(a.id));
}

function takeReport(job: ProjectEnvironmentJob, unmatched: Map<string, ProjectEnvironmentHistory[]>): ProjectEnvironmentHistory | null {
  const outcome = JOB_OUTCOME[job.status];
  if (outcome !== 'succeeded' && outcome !== 'failed') return null;
  const rows = unmatched.get(job.environmentId) ?? [];
  const since = Date.parse(job.claimedAt ?? job.createdAt);
  const index = rows.findIndex(row => row.artifact === job.artifact && Date.parse(row.at) >= since
    && SUCCESS_HISTORY.has(row.status) === (outcome === 'succeeded'));
  return index < 0 ? null : rows.splice(index, 1)[0];
}

function jobRun(job: ProjectEnvironmentJob, names: Map<string, string>, report: ProjectEnvironmentHistory | null,
  releases: readonly Release[] | null): DeployRun {
  const outcome = JOB_OUTCOME[job.status] ?? 'queued';
  const evidence = report?.evidence ?? null;
  const steps: DeployRunStep[] = [
    { name: 'Queued', state: 'succeeded', at: job.createdAt,
      detail: job.requestedBy ? `Requested by ${job.requestedBy}` : null },
    { name: 'Claimed by deploy host', state: job.claimedAt ? 'succeeded' : 'queued', at: job.claimedAt, detail: job.hostId },
    { name: job.isRollback ? 'Restore previous release and check health' : 'Deploy and check health',
      state: outcome === 'queued' ? 'queued' : outcome, at: report?.at ?? null, detail: evidenceLabel(evidence) },
  ];
  return {
    id: job.id, environmentId: job.environmentId, environmentName: names.get(job.environmentId) ?? job.environmentId,
    artifact: job.artifact, releaseVersion: releaseForArtifact(job.artifact, releases)?.version ?? null,
    kind: job.isRollback ? 'rollback' : 'deploy', outcome, requestedBy: job.requestedBy, hostId: job.hostId,
    queuedAt: job.createdAt, finishedAt: report?.at ?? null, evidence, steps,
  };
}

function historyRun(environmentId: string, names: Map<string, string>, row: ProjectEnvironmentHistory,
  releases: readonly Release[] | null): DeployRun {
  const outcome: DeploymentState = SUCCESS_HISTORY.has(row.status) ? 'succeeded' : 'failed';
  return {
    id: `history:${environmentId}:${row.at}`, environmentId, environmentName: names.get(environmentId) ?? environmentId,
    artifact: row.artifact, releaseVersion: releaseForArtifact(row.artifact, releases)?.version ?? null,
    kind: row.status === 'rolled-back' ? 'rollback' : 'deploy', outcome, requestedBy: null, hostId: null,
    queuedAt: row.at, finishedAt: row.at, evidence: row.evidence,
    steps: [{ name: 'Deploy and check health', state: outcome, at: row.at, detail: evidenceLabel(row.evidence) }],
  };
}

/** One status card per declared environment, in the server's Staging-then-Production order. */
export function buildEnvironmentCards(
  data: ProjectEnvironments,
  runs: readonly DeployRun[],
  releases: readonly Release[] | null,
  health: readonly DeployHealthCheckSummary[] | null,
): EnvironmentCardView[] {
  return data.environments.map(({ declaration, history }) => {
    const live = history.find(row => SUCCESS_HISTORY.has(row.status)) ?? null;
    const release = releaseForArtifact(live?.artifact ?? null, releases);
    const check = health?.find(item => item.environmentId === declaration.id);
    const pending = data.promotions.find(item => item.status === 'pending' && item.productionId === declaration.id);
    return {
      id: declaration.id,
      name: declaration.name,
      kind: declaration.kind,
      address: declaration.address,
      artifact: live?.artifact ?? null,
      releaseVersion: release?.version ?? null,
      deployedAt: live?.at ?? null,
      cards: cardsFor(live?.artifact ?? null, release, releases),
      health: check
        ? { status: 'checks', state: check.state, checkCount: check.checkCount, checkedAt: check.checkedAtUtc }
        : { status: 'no-checks' },
      lastRun: runs.find(run => run.environmentId === declaration.id) ?? null,
      awaitingApproval: pending?.artifact ?? null,
    };
  });
}

function cardsFor(artifact: string | null, release: Release | null, releases: readonly Release[] | null): DeployCards {
  if (!artifact) return { status: 'unknown', reason: 'Nothing deployed yet' };
  if (!releases) return { status: 'unknown', reason: 'Card lists arrive with release records' };
  if (!release) return { status: 'unknown', reason: 'No release record for this artifact' };
  return { status: 'known', keys: release.cardKeys };
}

export function filterDeployRuns(runs: readonly DeployRun[], filter: DeployRunFilter, now: number): DeployRun[] {
  const oldest = now - PERIOD_MS[filter.period];
  return runs.filter(run => (filter.environmentId === 'all' || run.environmentId === filter.environmentId)
    && (filter.outcome === 'all' || run.outcome === filter.outcome)
    && Date.parse(run.queuedAt) >= oldest);
}
