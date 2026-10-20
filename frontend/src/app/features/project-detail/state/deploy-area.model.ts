import { InjectionToken } from '@angular/core';
import type { ProjectEnvironments } from '../../../models/project-overview.model';
import type { EnvironmentHealthResponse } from '../../environment-health/models/environment-health.model';

// BD-1 wire contract served by AGT-3201 under /api/projects/{project}/deploy
// (backend/Features/Deploy/DeployContract.cs). States, triggers and health
// arrive as strings and are mapped explicitly below; an unknown value shows as
// unknown instead of borrowing another state's meaning.

export interface DeployEnvironment {
  id: string;
  projectId: string;
  name: string;
  order: number;
  kind: string;
}

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

export interface DeploymentStep {
  name: string;
  state: string;
  startedAtUtc: string | null;
  finishedAtUtc: string | null;
  logRef: string;
}

export interface Deployment {
  id: string;
  releaseId: string;
  environmentId: string;
  state: string;
  startedAtUtc: string | null;
  finishedAtUtc: string | null;
  approvedBy: string | null;
  steps: DeploymentStep[];
  trigger: string;
  requestedBy: string | null;
  createdAtUtc: string;
  interventionExpiresAtUtc?: string | null;
  failedStep?: string | null;
  interventionDecidedBy?: string | null;
  interventionDecision?: string | null;
}

export interface EnvironmentStatus {
  environment: DeployEnvironment;
  currentRelease: Release | null;
  currentDeployment: Deployment | null;
  newerRelease: Release | null;
  cardsSinceLastDeploy: string[];
  health: string;
  intervention?: { deploymentId: string; failedStep: string; expiresAtUtc: string | null;
    decision: string | null; decidedBy: string | null } | null;
}

export interface DeploymentLogLine {
  step: string;
  atUtc: string;
  message: string;
}

export interface DeploymentLogResponse {
  deploymentId: string;
  lines: DeploymentLogLine[];
}

/** Every `DeploymentStates` value of the contract. */
export const DEPLOYMENT_STATES = ['queued', 'running', 'succeeded', 'failed', 'cancelled', 'awaiting-approval', 'waiting-for-intervention'] as const;
export type DeploymentState = (typeof DEPLOYMENT_STATES)[number];
export type RunState = DeploymentState | 'unknown';
/** Step states reuse the run vocabulary plus `pending` and `skipped`. */
export type StepState = RunState | 'pending' | 'skipped';

export const RUN_STATE_LABEL: Record<RunState, string> = {
  queued: 'Queued',
  running: 'Running',
  succeeded: 'Succeeded',
  failed: 'Failed',
  cancelled: 'Cancelled',
  'awaiting-approval': 'Awaiting approval',
  'waiting-for-intervention': 'Waiting for intervention',
  unknown: 'Unknown state',
};

export const STEP_STATE_LABEL: Record<StepState, string> = { ...RUN_STATE_LABEL, pending: 'Pending', skipped: 'Skipped' };

const STEP_KIND_LABEL: Record<string, string> = {
  'static-site': 'Static site', container: 'Container', 'publish-static': 'Publish static',
  'start-container': 'Start container', run: 'Run', health: 'Health check', approval: 'Approval',
};
export function stepKindLabel(kind: string): string { return STEP_KIND_LABEL[kind] ?? kind; }

export function runState(raw: string): RunState {
  return (DEPLOYMENT_STATES as readonly string[]).includes(raw) ? raw as DeploymentState : 'unknown';
}

export function stepState(raw: string): StepState {
  return raw === 'pending' || raw === 'skipped' ? raw : runState(raw);
}

export const DEPLOY_TRIGGERS = ['deploy', 'redeploy', 'rollback', 'automatic-rollback'] as const;
const TRIGGER_LABEL: Record<(typeof DEPLOY_TRIGGERS)[number], string> = {
  deploy: 'Deploy',
  redeploy: 'Redeploy',
  rollback: 'Rollback',
  'automatic-rollback': 'Automatic rollback',
};

/** A trigger this build does not know keeps its wire name. */
export function triggerLabel(raw: string): string {
  return (DEPLOY_TRIGGERS as readonly string[]).includes(raw) ? TRIGGER_LABEL[raw as keyof typeof TRIGGER_LABEL] : raw;
}

export type HealthState = 'healthy' | 'unhealthy' | 'unknown' | 'up' | 'degraded' | 'down' | 'invalid';
/** AGT-3201 `EnvironmentStatus.health`. */
const STATUS_HEALTH: Record<string, HealthState> = { healthy: 'healthy', unhealthy: 'unhealthy', unknown: 'unknown' };
/** AGT-3176 `environments[].health`, from the latest host report. */
const HOST_HEALTH: Record<string, HealthState> = { healthy: 'healthy', failed: 'unhealthy', unknown: 'unknown' };
/** AGT-3176 history statuses: what a host report says about the run it closed. */
const HOST_REPORT: Record<string, { state: DeploymentState; trigger: string }> = {
  healthy: { state: 'succeeded', trigger: 'deploy' },
  'rolled-back': { state: 'succeeded', trigger: 'rollback' },
  failed: { state: 'failed', trigger: 'deploy' },
};

export const HEALTH_LABEL: Record<HealthState, string> = { healthy: 'Healthy', unhealthy: 'Unhealthy', unknown: 'No data', up: 'Up', degraded: 'Degraded', down: 'Down', invalid: 'Misconfigured' };

/** Actions the lifecycle accepts per state (AGT-3201 StandardLifecyclePolicy); the server still decides. */
export type DeployRunAction = 'approve' | 'reject' | 'cancel' | 'retry' | 'skip' | 'abort';
export function runActions(state: RunState): DeployRunAction[] {
  if (state === 'awaiting-approval') return ['approve', 'reject', 'cancel'];
  if (state === 'waiting-for-intervention') return ['retry', 'abort'];
  return state === 'queued' || state === 'running' ? ['cancel'] : [];
}

/**
 * One panel's data. `unavailable` means the route is not on this server yet
 * (AGT-3201 before it ships); it is not an error.
 */
export type DeployPanel<T> =
  | { status: 'loading' }
  | { status: 'ready'; value: T }
  | { status: 'unavailable' }
  | { status: 'error'; message: string };

export function ready<T>(panel: DeployPanel<T>): T | null {
  return panel.status === 'ready' ? panel.value : null;
}

/** Epoch-millisecond clock for ages and the history period filter; tests replace it. */
export const DEPLOY_CLOCK = new InjectionToken<() => number>('DEPLOY_CLOCK', {
  providedIn: 'root',
  factory: () => () => Date.now(),
});

export interface DeployRunStep {
  name: string;
  state: StepState;
  startedAt: string | null;
  finishedAt: string | null;
}

export interface DeployRun {
  id: string;
  environmentId: string;
  environmentName: string;
  releaseId: string;
  releaseVersion: string | null;
  trigger: string;
  state: RunState;
  requestedBy: string | null;
  approvedBy: string | null;
  createdAt: string;
  startedAt: string | null;
  finishedAt: string | null;
  interventionExpiresAt?: string | null;
  failedStep?: string | null;
  interventionDecidedBy?: string | null;
  interventionDecision?: string | null;
  steps: DeployRunStep[];
}

export type DeployCards = { status: 'known'; keys: string[] } | { status: 'unknown'; reason: string };
export type DeployHealth = { status: 'reported'; state: HealthState } | { status: 'no-checks' };

/** The latest run of a card: an AGT-3201 deployment, or the last AGT-3176 host report without it. */
export interface CardRun {
  deploymentId: string | null;
  trigger: string;
  state: RunState;
  at: string;
}

export interface CardApproval {
  deploymentId: string | null;
  subject: string;
  requestedBy: string | null;
}

export interface EnvironmentCardView {
  id: string;
  name: string;
  address: string | null;
  artifact: string | null;
  releaseVersion: string | null;
  deployedAt: string | null;
  cards: DeployCards;
  newer: { version: string; cardKeys: string[] } | null;
  health: DeployHealth;
  lastRun: CardRun | null;
  awaitingApproval: CardApproval | null;
  intervention?: EnvironmentStatus['intervention'];
}

/** Ready panel values; null means that source is loading, failed, or absent. */
export interface DeploySources {
  environments: ProjectEnvironments | null;
  statuses: EnvironmentStatus[] | null;
  deployments: Deployment[] | null;
  releases: Release[] | null;
  health?: EnvironmentHealthResponse | null;
}

export type DeployPeriod = '24h' | '7d' | '30d' | 'all';
export interface DeployRunFilter {
  environmentId: string;
  state: RunState | 'all';
  period: DeployPeriod;
}
export const ALL_DEPLOY_RUNS: DeployRunFilter = { environmentId: 'all', state: 'all', period: 'all' };

const PERIOD_MS: Record<DeployPeriod, number> = {
  '24h': 86_400_000,
  '7d': 7 * 86_400_000,
  '30d': 30 * 86_400_000,
  all: Number.POSITIVE_INFINITY,
};

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

const newestFirst = (a: Deployment, b: Deployment) =>
  Date.parse(b.createdAtUtc) - Date.parse(a.createdAtUtc) || b.id.localeCompare(a.id);

/** Environment names by id: AGT-3176 declarations first, then AGT-3201 environments. */
export function environmentNames(sources: Pick<DeploySources, 'environments' | 'statuses'>): Map<string, string> {
  const names = new Map((sources.statuses ?? []).map(item => [item.environment.id, item.environment.name]));
  for (const item of sources.environments?.environments ?? []) names.set(item.declaration.id, item.declaration.name);
  return names;
}

/** AGT-3201 deploy runs, newest first, with environment names and release versions. */
export function buildDeployRuns(deployments: readonly Deployment[], names: ReadonlyMap<string, string>,
  releases: readonly Release[] | null): DeployRun[] {
  const versions = new Map((releases ?? []).map(release => [release.id, release.version]));
  return [...deployments].sort(newestFirst).map(item => ({
    id: item.id,
    environmentId: item.environmentId,
    environmentName: names.get(item.environmentId) ?? item.environmentId,
    releaseId: item.releaseId,
    releaseVersion: versions.get(item.releaseId) ?? null,
    trigger: item.trigger,
    state: runState(item.state),
    requestedBy: item.requestedBy,
    approvedBy: item.approvedBy,
    createdAt: item.createdAtUtc,
    startedAt: item.startedAtUtc,
    finishedAt: item.finishedAtUtc,
    interventionExpiresAt: item.interventionExpiresAtUtc ?? null,
    failedStep: item.failedStep ?? null,
    interventionDecidedBy: item.interventionDecidedBy ?? null,
    interventionDecision: item.interventionDecision ?? null,
    steps: item.steps.map(step => ({
      name: step.name, state: stepState(step.state), startedAt: step.startedAtUtc, finishedAt: step.finishedAtUtc,
    })),
  }));
}

interface CardBase {
  id: string;
  name: string;
  address: string | null;
  healthUrl: string | null;
  hasChecks: boolean;
  view: ProjectEnvironments['environments'][number] | null;
}

function cardBases(sources: DeploySources): CardBase[] {
  if (sources.environments) {
    return sources.environments.environments.map(view => ({
      id: view.declaration.id, name: view.declaration.name, address: view.declaration.address,
      healthUrl: view.declaration.healthUrl, view,
      hasChecks: (view.declaration.healthChecks?.length ?? 0) > 0,
    }));
  }
  return [...(sources.statuses ?? [])].sort((a, b) => a.environment.order - b.environment.order).map(status => ({
    id: status.environment.id, name: status.environment.name, address: null, healthUrl: null, hasChecks: false, view: null,
  }));
}

/**
 * One status card per declared environment, in the server's Staging-then-Production
 * order. AGT-3201 status and runs win; without them the card reads the AGT-3176
 * environment state (deployed artifact, host health, last report, pending promotion).
 */
export function buildEnvironmentCards(sources: DeploySources): EnvironmentCardView[] {
  return cardBases(sources).map(base => {
    const status = sources.statuses?.find(item => item.environment.id === base.id) ?? null;
    const pageRuns = sources.deployments?.filter(item => item.environmentId === base.id) ?? [];
    const statusRun = status?.currentDeployment ?? null;
    const runs = sources.deployments || statusRun
      ? [...pageRuns, ...(statusRun && !pageRuns.some(item => item.id === statusRun.id) ? [statusRun] : [])].sort(newestFirst)
      : null;
    const report = base.view?.history.find(row => HOST_REPORT[row.status]?.state === 'succeeded'
      && (!base.view?.deployedArtifact || row.artifact === base.view.deployedArtifact)) ?? null;
    const artifact = status?.intervention
      ? status.currentRelease?.artifactDigests[0] ?? null
      : base.view?.deployedArtifact ?? status?.currentRelease?.artifactDigests[0] ?? null;
    const statusRelease = status?.currentRelease ?? null;
    const release = releaseForArtifact(artifact, sources.releases)
      ?? (statusRelease?.artifactDigests.some(item => digestOf(item) === digestOf(artifact ?? '')) ? statusRelease : null);
    const current = status?.currentDeployment?.state === 'succeeded' && status.currentDeployment.releaseId === release?.id
      ? status.currentDeployment : null;
    const matchingRun = runs?.filter(item => item.state === 'succeeded' && item.releaseId === release?.id)
      .sort((a, b) => Date.parse(b.finishedAtUtc ?? '') - Date.parse(a.finishedAtUtc ?? ''))[0] ?? null;
    const deployedAt = [current?.finishedAtUtc, matchingRun?.finishedAtUtc, report?.at]
      .filter((at): at is string => !!at).sort((a, b) => Date.parse(b) - Date.parse(a))[0] ?? null;
    const releaseRun = runs?.length ? lastRunOf(runs) : null;
    const hostRun = lastReportOf(base.view);
    const lastRun = hostRun && (!releaseRun || Date.parse(hostRun.at) > Date.parse(releaseRun.at)) ? hostRun : releaseRun;
    return {
      id: base.id,
      name: base.name,
      address: base.address,
      artifact,
      releaseVersion: release?.version ?? null,
      deployedAt,
      cards: cardsFor(artifact !== null || release !== null, release, sources.releases !== null || status !== null),
      newer: status?.newerRelease ? { version: status.newerRelease.version, cardKeys: status.cardsSinceLastDeploy } : null,
      health: healthFor(status, base, sources.health),
      lastRun,
      awaitingApproval: approvalFor(base, runs, sources),
      intervention: status?.intervention,
    };
  });
}

function cardsFor(deployed: boolean, release: Release | null, releaseRecordsKnown: boolean): DeployCards {
  if (!deployed) return { status: 'unknown', reason: 'Nothing deployed yet' };
  if (release) return { status: 'known', keys: release.cardKeys };
  return releaseRecordsKnown
    ? { status: 'unknown', reason: 'No release record for this artifact' }
    : { status: 'unknown', reason: 'Card lists arrive with release records' };
}

function healthFor(status: EnvironmentStatus | null, base: CardBase, health?: EnvironmentHealthResponse | null): DeployHealth {
  const observed = health?.environments.find(item => item.environmentId === base.id);
  if (observed && (observed.checks.length > 0 || observed.status === 'invalid'))
    return { status: 'reported', state: observed.status };
  if (base.view && !base.healthUrl && !base.hasChecks) return { status: 'no-checks' };
  // AGT-3201 health wins when it knows; it reads a host rollback as unknown, AGT-3176 as healthy.
  const reported = status ? STATUS_HEALTH[status.health] ?? 'unknown' : 'unknown';
  const host = base.view ? HOST_HEALTH[base.view.health] ?? 'unknown' : 'unknown';
  const state = reported !== 'unknown' ? reported : host;
  // Without a declared health URL there is nothing to check.
  return { status: 'reported', state };
}

function lastRunOf(runs: readonly Deployment[]): CardRun | null {
  const run = runs[0];
  return run
    ? { deploymentId: run.id, trigger: run.trigger, state: runState(run.state), at: run.finishedAtUtc ?? run.startedAtUtc ?? run.createdAtUtc }
    : null;
}

function lastReportOf(view: CardBase['view']): CardRun | null {
  const row = view?.history[0];
  if (!row) return null;
  const report = HOST_REPORT[row.status];
  return { deploymentId: null, trigger: report?.trigger ?? 'deploy', state: report?.state ?? 'unknown', at: row.at };
}

function approvalFor(base: CardBase, runs: readonly Deployment[] | null, sources: DeploySources): CardApproval | null {
  // AGT-3201 adopts pending AGT-3176 promotions into runs that await approval.
  if (runs) {
    const waiting = runs.find(item => item.state === 'awaiting-approval');
    if (waiting) {
    const status = sources.statuses?.find(item => item.environment.id === base.id);
    const release = sources.releases?.find(item => item.id === waiting.releaseId)
      ?? [status?.newerRelease, status?.currentRelease].find(item => item?.id === waiting.releaseId);
    return { deploymentId: waiting.id, subject: release ? `Release ${release.version}` : 'A release', requestedBy: waiting.requestedBy };
    }
  }
  const pending = sources.environments?.promotions.find(item => item.status === 'pending' && item.productionId === base.id);
  return pending ? { deploymentId: null, subject: shortArtifact(pending.artifact), requestedBy: null } : null;
}

export function filterDeployRuns(runs: readonly DeployRun[], filter: DeployRunFilter, now: number): DeployRun[] {
  const oldest = now - PERIOD_MS[filter.period];
  return runs.filter(run => (filter.environmentId === 'all' || run.environmentId === filter.environmentId)
    && (filter.state === 'all' || run.state === filter.state)
    && Date.parse(run.createdAt) >= oldest);
}

export interface DeployStepLog {
  step: string;
  state: StepState | null;
  startedAt: string | null;
  finishedAt: string | null;
  lines: DeploymentLogLine[];
}

/** Log lines under their step, in step order; lines of a step the run does not list follow at the end. */
export function groupStepLog(steps: readonly DeployRunStep[], lines: readonly DeploymentLogLine[]): DeployStepLog[] {
  const groups: DeployStepLog[] = steps.map(step => ({
    step: step.name, state: step.state, startedAt: step.startedAt, finishedAt: step.finishedAt,
    lines: lines.filter(line => line.step === step.name),
  }));
  const known = new Set(steps.map(step => step.name));
  for (const line of lines.filter(item => !known.has(item.step))) {
    let group = groups.find(item => item.step === line.step && item.state === null);
    if (!group) {
      group = { step: line.step, state: null, startedAt: null, finishedAt: null, lines: [] };
      groups.push(group);
    }
    group.lines.push(line);
  }
  return groups;
}
