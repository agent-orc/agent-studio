import path from 'node:path';

export const integrationProfile = 'remote-integration';
export const defaultAutonomyDurationSeconds = 25;
export const realAutonomyDurationSeconds = 600;
export const unitServices = Object.freeze({
  studio: 'studio',
  'task-server': 'task-server',
  runner: 'agent-runner'
});

const defaultPorts = Object.freeze({
  taskServer: 19741,
  studio: 19742,
  faultControl: 19743,
  runnerControl: 19744
});

export function validateRunId(runId) {
  if (!/^[a-z0-9][a-z0-9-]{0,31}$/.test(runId ?? '')) {
    throw new Error('Run id must be 1-32 lowercase letters, digits, or hyphens, starting with a letter or digit.');
  }
  return runId;
}

export function validatePorts(ports) {
  const values = Object.values(ports);
  if (values.some(value => !Number.isInteger(value) || value < 1024 || value > 65535)) {
    throw new Error('Harness ports must be unique integers between 1024 and 65535.');
  }
  if (new Set(values).size !== values.length) throw new Error('Harness ports must be unique.');
  return ports;
}

export function createAutonomyPolicy(
  durationSeconds,
  { machineBound = false } = {}) {
  if (!Number.isInteger(durationSeconds) || durationSeconds < 20 || durationSeconds > 3600) {
    throw new Error('Autonomy duration must be an integer between 20 and 3600 seconds.');
  }
  if (durationSeconds >= realAutonomyDurationSeconds && !machineBound) {
    throw new Error(
      'An autonomy duration of ten minutes or more requires the explicit MachineBound marker.');
  }
  if (machineBound && durationSeconds < realAutonomyDurationSeconds) {
    throw new Error(
      'The MachineBound autonomy canary must run for at least ten real minutes.');
  }
  const requiredDurationMs = durationSeconds * 1000;
  return Object.freeze({
    mode: machineBound
      ? 'machine-bound-ten-minute'
      : 'short-card',
    requiredDurationMs,
    minimumWorkUnits: Math.max(2, Math.floor(durationSeconds / 12)),
    usefulWorkTailToleranceMs: 15_000
  });
}

export function createComposePlan({ repoRoot, runId, ports = defaultPorts }) {
  validateRunId(runId);
  validatePorts(ports);
  const project = `agt2394-rts-${runId}`;
  const root = path.resolve(repoRoot, '.tmp', 'remote-test-suite', 'compose', runId);
  const suiteRoot = path.resolve(repoRoot, 'tools', 'remote-test-suite');
  const environmentFile = path.join(root, 'compose.env');
  const tokenFile = path.join(root, 'auth.token');
  const archiveCredentialFile = path.join(root, 'archive-s3.json');
  const evidenceRoot = path.join(root, 'evidence');
  return {
    runId,
    project,
    profile: integrationProfile,
    repoRoot: path.resolve(repoRoot),
    suiteRoot,
    composeFile: path.join(suiteRoot, 'compose.yaml'),
    root,
    environmentFile,
    tokenFile,
    archiveCredentialFile,
    evidenceRoot,
    ports: { ...ports },
    urls: {
      taskServer: `http://127.0.0.1:${ports.taskServer}`,
      studio: `http://127.0.0.1:${ports.studio}`,
      faultControl: `http://127.0.0.1:${ports.faultControl}`,
      runnerControl: `http://127.0.0.1:${ports.runnerControl}`
    },
    resources: {
      containers: ['task-server', 'minio', 'minio-init', 'fault-proxy', 'agent-runner', 'studio']
        .map(service => `${project}-${service}-1`),
      network: `${project}-network`,
      volumes: [`${project}-task-server-data`, `${project}-runner-workspace`, `${project}-minio-data`],
      images: ['task-server', 'fault-proxy', 'agent-runner', 'studio']
        .map(service => `${project}-${service}:local`)
    },
    identityLabel: `com.agentstudio.remote-harness.identity=${project}`,
    neverTouches: [
      'agent-studio',
      'agent-taskboard-stable/',
      'agent-taskboard-workspace/projects/',
      'agent-taskboard-workspace/.metadata/'
    ]
  };
}

export function composeCommand(plan, ...args) {
  return [
    'docker', 'compose',
    '--project-name', plan.project,
    '--env-file', plan.environmentFile,
    '--file', plan.composeFile,
    '--profile', plan.profile,
    ...args
  ];
}

export function unitOperationPlan(operation, unit, { force = false } = {}) {
  const service = unitServices[unit];
  if (!service) throw new Error(`Unit must be one of: ${Object.keys(unitServices).join(', ')}.`);
  if (operation === 'partition' || operation === 'heal') {
    const action = operation === 'partition' ? 'partition' : 'heal';
    const links = unit === 'task-server' ? ['runner', 'studio'] : [unit];
    return links.map(link => ({
      kind: 'proxy',
      method: 'POST',
      route: `/links/${link}/${action}`
    }));
  }
  if (operation === 'stop') {
    return [
      ...(unit === 'task-server' ? drainSteps(force) : []),
      { kind: 'compose', args: ['stop', '--timeout', '20', service] }
    ];
  }
  if (operation === 'restart') {
    return [
      ...(unit === 'task-server' ? drainSteps(force) : []),
      { kind: 'compose', args: ['restart', '--timeout', '20', service] },
      ...(unit === 'task-server'
        ? restoreNormalSteps()
        : [{ kind: 'wait-unit', unit }])
    ];
  }
  if (operation === 'replace') {
    return [
      ...(unit === 'task-server' ? drainSteps(force) : []),
      {
        kind: 'compose',
        args: ['up', '--detach', '--no-deps', '--force-recreate', '--wait', '--wait-timeout', '90', service]
      },
      ...(unit === 'task-server' ? restoreNormalSteps() : [])
    ];
  }
  throw new Error('Operation must be stop, restart, replace, partition, or heal.');
}

function drainSteps(force) {
  return [
    {
      kind: 'api',
      method: 'PUT',
      route: '/api/v1/management/mode',
      body: { mode: 1, reason: 'remote Compose harness bounded unit operation' }
    },
    {
      kind: 'prepare-shutdown',
      method: 'POST',
      route: '/api/v1/management/prepare-shutdown',
      body: { reason: 'remote Compose harness bounded unit operation' },
      allowUnsafe: force
    }
  ];
}

function restoreNormalSteps() {
  return [
    { kind: 'wait-ready' },
    {
      kind: 'api',
      method: 'PUT',
      route: '/api/v1/management/mode',
      body: { mode: 0, reason: 'remote Compose harness unit operation completed' }
    }
  ];
}

export function assertRollingEvidence(evidence) {
  const assertions = [];
  check(assertions, 'reference-task-accepted', evidence.reference?.accepted === true,
    'The deterministic reference task must be accepted.');
  check(assertions, 'studio-does-not-own-execution',
    sameAttempt(evidence.active, evidence.afterStudio),
    'Studio replacement must preserve the active run id and fence.');
  check(assertions, 'runner-replacement-preserves-attempt',
    sameAttempt(evidence.active, evidence.afterRunner),
    'Runner replacement must reattach the same active run id and fence.');
  check(assertions, 'task-server-restart-quarantines',
    evidence.quarantined?.task?.state === '3-progress'
      && (evidence.quarantined?.runs?.some(run =>
        run.status === 'process-unknown')
        || evidence.quarantined?.events?.some(event =>
          event.kind === 'lifecycle.process-unknown'
          && evidence.quarantined?.runs?.some(run => run.runId === event.runId))),
    'Task Server replacement must quarantine unresolved authority as process-unknown.');
  check(assertions, 'stale-write-rejected', evidence.staleWriteStatus === 409,
    'The pre-restart fence must not complete after recovery.');

  const recoveredRuns = evidence.finalHistory?.runs ?? [];
  const fences = recoveredRuns.map(run => run.fence).filter(value => value !== null);
  check(assertions, 'no-duplicate-claims',
    recoveredRuns.length === 2
      && new Set(recoveredRuns.map(run => run.runId)).size === 2
      && new Set(fences).size === fences.length,
    'Recovery must create exactly one higher-fenced replacement attempt.');
  check(assertions, 'lost-queue-prevented', evidence.finalHistory?.task?.state === '2-ready',
    'The released recovery attempt must return the task to Ready.');
  check(assertions, 'no-zombie-authority', evidence.finalShutdown?.safeToStop === true
    && evidence.finalShutdown?.unresolvedAttempts === 0,
  'Final drain must find no active or process-unknown authority.');
  check(assertions, 'no-phantom-deliveries',
    evidence.status?.outboxBacklog === 0
      && (evidence.outboxes ?? []).every(item => item.backlogCount === 0),
    'Task Server and Runner outboxes must have no phantom backlog.');
  check(assertions, 'audit-sequences-unique',
    unique((evidence.audit ?? []).map(item => item.sequence)),
    'Audit sequence numbers must remain unique.');
  check(assertions, 'invariants-clear', evidence.invariants?.pendingRunnerActions === 0,
    'Invariant reconciliation must leave no pending Runner actions.');
  return assertions;
}

export function assertAutonomyEvidence(evidence, policy) {
  if (!policy) throw new Error('An explicit autonomy policy is required.');
  const assertions = [];
  check(assertions, 'configured-real-duration',
    evidence?.durationMs >= policy.requiredDurationMs,
    `The Task Server partition must last at least ${policy.requiredDurationMs} real milliseconds.`);
  check(assertions, 'multiple-preclaimed-slots',
    evidence?.slots?.length >= 2
      && new Set(evidence.slots.map(slot => slot.runId)).size === evidence.slots.length,
    'At least two already-claimed remote slots must execute through the outage.');
  check(assertions, 'useful-work-throughout-outage',
    (evidence?.slots ?? []).every(slot =>
      slot.workUnits >= policy.minimumWorkUnits
      && Date.parse(slot.lastUsefulWorkAt)
        >= Date.parse(evidence.requiredWorkThroughAt) - policy.usefulWorkTailToleranceMs),
    'Every slot must record useful work through the configured partition.');
  check(assertions, 'no-unsafe-new-claim',
    (evidence?.claimDuringPartitionStatus === 409
      || evidence?.claimDuringPartitionStatus >= 500)
      && evidence?.unclaimedTask?.state === '2-ready',
    'Transport uncertainty must not admit the waiting task.');
  check(assertions, 'authority-before-replay',
    (evidence?.slots ?? []).every(slot =>
      Date.parse(slot.reconciledAt) >= Date.parse(evidence.partitionEndedAt)
      && Date.parse(slot.completedAt) >= Date.parse(slot.reconciledAt)),
    'Every slot must reconcile its exact fence before replay completes.');
  check(assertions, 'results-and-terminal-delivered-once',
    (evidence?.histories ?? []).every((history, index) =>
      history.task?.state === '4-auto-review'
      && history.runs?.length === 1
      && history.runs[0]?.resultSha === evidence.slots[index]?.resultSha
      && unique((history.events ?? []).map(item => item.idempotencyKey))
      && unique((history.artifacts ?? []).map(item => item.idempotencyKey))
      && history.artifacts?.length === 1),
    'Events, artifacts, Result SHA, terminal, and completion must survive recovery without duplication.');
  check(assertions, 'outboxes-drained',
    (evidence?.slots ?? []).every(slot =>
      slot.backlogCount === 0
      && slot.lastSequence === slot.lastAcknowledgedSequence),
    'Every recovered Runner outbox must be fully acknowledged.');
  check(assertions, 'no-zombie-or-phantom-generation',
    (evidence?.slots ?? []).every(slot =>
      slot.phase === 'completed'
      && slot.generation?.alive === false
      && slot.generation?.deathProven === true),
    'Recovered slots must close their one contained generation with no zombie replacement.');
  return assertions;
}

function sameAttempt(left, right) {
  const leftRun = left?.runs?.[0];
  const rightRun = right?.runs?.[0];
  return leftRun && rightRun
    && leftRun.runId === rightRun.runId
    && leftRun.fence === rightRun.fence
    && right?.task?.state === '3-progress';
}

function unique(values) {
  return new Set(values).size === values.length;
}

function check(assertions, name, passed, detail) {
  assertions.push({ name, passed, detail });
  if (!passed) throw new Error(`Harness assertion failed: ${name}. ${detail}`);
}

export function redact(value, secrets) {
  let result = String(value);
  for (const secret of secrets.filter(Boolean)) result = result.split(secret).join('[REDACTED]');
  return result;
}

export { defaultPorts };
