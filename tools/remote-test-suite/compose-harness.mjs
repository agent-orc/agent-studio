#!/usr/bin/env node
import { createHash, randomBytes } from 'node:crypto';
import { spawn } from 'node:child_process';
import {
  chmod,
  copyFile,
  mkdir,
  readFile,
  rm,
  stat,
  writeFile
} from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  assertAutonomyEvidence,
  assertRollingEvidence,
  composeCommand,
  createAutonomyPolicy,
  createComposePlan,
  defaultAutonomyDurationSeconds,
  defaultPorts,
  redact,
  unitOperationPlan
} from './compose-core.mjs';
import { sanitizeEnvironmentValues } from './report-capture.mjs';

const suiteRoot = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(suiteRoot, '..', '..');
const args = parseArgs(process.argv.slice(2));
const plan = createComposePlan({ repoRoot, runId: args.runId, ports: args.ports });
const autonomyPolicy = createAutonomyPolicy(
  args.autonomyDurationSeconds,
  { machineBound: args.machineBound });

if (args.command === 'inspect') {
  console.log(JSON.stringify({
    dryRun: true,
    ...plan,
    autonomyPolicy,
    acceptanceSequence: [
      'provision',
      's3-retention-archive-verify-restore',
      'reference-task',
      `${autonomyPolicy.mode}-multi-slot-task-server-partition`,
      'studio-partition-and-replacement',
      'runner-partition-and-replacement',
      'task-server-partition-and-honest-fence-recovery',
      'evidence-export',
      'identity-scoped-teardown'
    ],
    operation: args.operation && args.unit
      ? unitOperationPlan(args.operation, args.unit, { force: args.force })
      : null
  }, null, 2));
  process.exit(0);
}

let authToken = null;
let stackStarted = false;
let result;
try {
  if (args.command === 'run' || args.command === 'up') {
    await initializeRunRoot();
    await ensureNoIdentityResources();
    await validateCompose();
    stackStarted = true;
    await compose(['up', '--build', '--detach', '--wait', '--wait-timeout', '180'], 1_200_000);
    await waitForJson(`${plan.urls.taskServer}/readyz`, value => value.status === 'ready', 90_000);
    await waitForJson(`${plan.urls.runnerControl}/healthz`, value => value.status === 'ready', 60_000);
    await waitForText(`${plan.urls.studio}/`, text => text.includes('<app-root'), 60_000);
  } else {
    await loadExistingEnvironment();
    stackStarted = await hasIdentityContainers();
  }

  if (args.command === 'up') {
    result = { status: 'ready', project: plan.project, urls: plan.urls, root: plan.root };
  } else if (args.command === 'down') {
    await collectEvidence('manual-down');
    await executeUnitOperation('stop', 'task-server', args.force);
    await teardown();
    stackStarted = false;
    result = { status: 'removed', project: plan.project, evidenceRoot: plan.evidenceRoot };
  } else if (args.command === 'control') {
    await executeUnitOperation(args.operation, args.unit, args.force);
    result = {
      status: 'completed',
      operation: args.operation,
      unit: args.unit,
      project: plan.project
    };
  } else if (args.command === 'run') {
    result = await runAcceptance();
  }
} catch (error) {
  await writeFailure(error).catch(() => {});
  throw error;
} finally {
  if (args.command === 'run' && stackStarted && !args.keep) {
    await collectEvidence('final').catch(error => progress(`evidence collection failed: ${error.message}`));
    await teardown();
    stackStarted = false;
  }
}

console.log(JSON.stringify(result, null, 2));

async function runAcceptance() {
  progress('capturing component and container versions');
  const versions = await captureVersions();
  await writeEvidence('versions.json', versions);

  progress('running the disposable retention archive and restore scenario');
  const retention = await runRetentionScenario();

  progress('running deterministic reference task through the isolated Task Server');
  const reference = await runReferenceScenario();

  progress(
    `starting the ${args.autonomyDurationSeconds}-second multi-slot Task Server outage canary`);
  const autonomy = await runAutonomyCanary();

  progress('claiming the canary task held Ready during transport uncertainty');
  const rollIds = {
    project: autonomy.unclaimedTask.projectId,
    task: autonomy.unclaimedTask.taskId
  };
  const rollingTask = autonomy.unclaimedTask;
  const claimed = await runner('/claim', { method: 'POST' });
  if (claimed.task.taskId !== rollingTask.taskId) {
    throw new Error(
      `Runner claimed ${claimed.task.taskId}; expected the canary waiting task ${rollingTask.taskId}.`);
  }
  const originalAuthority = structuredClone(claimed);
  await waitForRunner(value => value.renewals >= 1, 20_000);
  const active = await history(rollIds.project, rollingTask.taskId);

  progress('partitioning Studio without transferring execution ownership');
  const renewalBeforeStudioPartition = (await runner('/status')).renewals;
  await proxy('studio', 'partition');
  const studioShellDuringPartition = await fetch(`${plan.urls.studio}/`);
  if (!studioShellDuringPartition.ok) throw new Error('Studio static shell stopped during its Task Server partition.');
  const partitionedStudioApi = await fetch(`${plan.urls.studio}/api/v1/management/status`);
  if (partitionedStudioApi.ok) throw new Error('Studio API unexpectedly remained connected during partition.');
  await waitForRunner(value => value.renewals > renewalBeforeStudioPartition, 10_000);
  await proxy('studio', 'heal');
  await waitForJson(`${plan.urls.studio}/api/v1/management/status`, value => value.authorityReady === true, 20_000);

  progress('replacing Studio while the Runner keeps the active lease');
  const studioBefore = await containerId('studio');
  await executeUnitOperation('replace', 'studio');
  const studioAfter = await containerId('studio');
  assertChangedContainer('Studio', studioBefore, studioAfter);
  await waitForText(`${plan.urls.studio}/`, text => text.includes('<app-root'), 30_000);
  const afterStudio = await historyViaStudio(rollIds.project, rollingTask.taskId);

  progress('partitioning and healing the Runner transport');
  const runnerBeforePartition = await runner('/status');
  await proxy('runner', 'partition');
  await waitForRunner(value => value.renewalFailures > runnerBeforePartition.renewalFailures, 12_000);
  await proxy('runner', 'heal');
  await waitForRunner(value => value.renewals > runnerBeforePartition.renewals, 15_000);

  progress('partitioning and healing Task Server client links');
  const beforeServerPartition = await runner('/status');
  await executeUnitOperation('partition', 'task-server');
  await waitForRunner(value => value.renewalFailures > beforeServerPartition.renewalFailures, 12_000);
  const taskServerPartitionedStudio = await fetch(`${plan.urls.studio}/api/v1/management/status`);
  if (taskServerPartitionedStudio.ok) throw new Error('Studio reached Task Server while the Task Server links were partitioned.');
  await executeUnitOperation('heal', 'task-server');
  await waitForRunner(value => value.renewals > beforeServerPartition.renewals, 15_000);

  progress('replacing the Runner and reattaching persisted attempt authority');
  const runnerBefore = await containerId('agent-runner');
  await executeUnitOperation('replace', 'runner');
  const runnerAfter = await containerId('agent-runner');
  assertChangedContainer('Runner', runnerBefore, runnerAfter);
  await waitForRunner(value => value.claim?.run?.runId === originalAuthority.run.runId
    && value.claim?.lease?.fence === originalAuthority.lease.fence
    && value.renewals >= 1, 20_000);
  const afterRunner = await history(rollIds.project, rollingTask.taskId);

  progress('draining and replacing Task Server with active authority');
  const draining = await api('/api/v1/management/mode', {
    method: 'PUT',
    body: { mode: 1, reason: 'remote Compose harness Task Server rolling update' }
  });
  const deferredShutdown = await api('/api/v1/management/prepare-shutdown', {
    method: 'POST',
    body: { reason: 'remote Compose harness active-authority update proof' }
  });
  if (deferredShutdown.safeToStop || deferredShutdown.unresolvedAttempts !== 1) {
    throw new Error('Task Server did not defer shutdown for active authority.');
  }
  const taskServerBefore = await containerId('task-server');
  await executeUnitOperation('replace', 'task-server', true);
  const taskServerAfter = await containerId('task-server');
  assertChangedContainer('Task Server', taskServerBefore, taskServerAfter);
  await waitForJson(`${plan.urls.taskServer}/readyz`, value => value.status === 'ready', 60_000);
  await api('/api/v1/management/mode', {
    method: 'PUT',
    body: { mode: 0, reason: 'remote Compose harness recovery inspection' }
  });
  const quarantined = await history(rollIds.project, rollingTask.taskId);
  const processUnknownEvent = quarantined.events.find(
    event => event.kind === 'lifecycle.process-unknown');
  const unknownRun = quarantined.runs.find(run => run.status === 'process-unknown')
    ?? quarantined.runs.find(run => run.runId === processUnknownEvent?.runId);
  await writeEvidence('rolling-quarantine-unasserted.json', quarantined);
  if (!unknownRun) {
    throw new Error('Task Server restart did not quarantine active authority.');
  }

  progress('proving old Runner containment ended and recovering with a higher fence');
  const staleRunnerContainer = await containerId('agent-runner');
  await executeUnitOperation('replace', 'runner');
  const recoveryRunnerContainer = await containerId('agent-runner');
  assertChangedContainer('Recovery Runner', staleRunnerContainer, recoveryRunnerContainer);
  await api(`/api/v1/management/attempts/${unknownRun.runId}/resolve-unknown`, {
    method: 'POST',
    body: {
      containmentProof: `Compose removed container ${staleRunnerContainer}; replacement ${recoveryRunnerContainer} has a distinct container identity`,
      resolution: 'requeue'
    }
  });
  await runner('/forget', { method: 'POST' });
  const replacementClaim = await runner('/claim', { method: 'POST' });
  if (replacementClaim.run.runId === originalAuthority.run.runId
      || replacementClaim.lease.fence <= originalAuthority.lease.fence) {
    throw new Error('Recovered attempt did not receive a distinct run and higher fence.');
  }

  const staleWrite = await apiRaw(`/api/v1/runs/${originalAuthority.run.runId}/completion`, {
    method: 'POST',
    body: {
      runnerId: originalAuthority.lease.runnerId,
      instanceId: originalAuthority.lease.instanceId,
      leaseId: originalAuthority.lease.leaseId,
      fence: originalAuthority.lease.fence,
      outcome: 'Done',
      summary: 'This stale completion must be rejected.',
      idempotencyKey: `${args.runId}:stale-completion`,
      sequence: 1
    }
  });
  if (staleWrite.status !== 409) {
    throw new Error(`Stale completion returned ${staleWrite.status}, expected 409.`);
  }
  await runner('/release', { method: 'POST' });
  const finalHistory = await history(rollIds.project, rollingTask.taskId);

  progress('asserting empty authority and delivery backlogs');
  await api('/api/v1/management/mode', {
    method: 'PUT',
    body: { mode: 1, reason: 'remote Compose harness final authority check' }
  });
  const finalShutdown = await api('/api/v1/management/prepare-shutdown', {
    method: 'POST',
    body: { reason: 'remote Compose harness final authority check' }
  });
  const status = await api('/api/v1/management/status');
  const outboxes = await api('/api/v1/management/outboxes');
  const audit = await api('/api/v1/management/audit');
  const invariants = await api('/api/v1/management/invariants');
  const evidence = {
    retention,
    reference,
    autonomy,
    active,
    afterStudio,
    afterRunner,
    draining,
    deferredShutdown,
    quarantined,
    staleWriteStatus: staleWrite.status,
    finalHistory,
    finalShutdown,
    status,
    outboxes,
    audit,
    invariants
  };
  const assertions = [
    ...retention.assertions,
    ...autonomy.assertions,
    ...assertRollingEvidence(evidence)
  ];
  const acceptance = {
    schemaVersion: 1,
    runId: args.runId,
    project: plan.project,
    completedAt: new Date().toISOString(),
    assertions,
    retention,
    reference,
    autonomy,
    rollingTask: {
      projectId: rollIds.project,
      taskId: rollingTask.taskId,
      originalRunId: originalAuthority.run.runId,
      recoveredRunId: replacementClaim.run.runId,
      originalFence: originalAuthority.lease.fence,
      recoveredFence: replacementClaim.lease.fence,
      finalState: finalHistory.task.state
    },
    taskServerUpdate: {
      oldContainerId: taskServerBefore,
      newContainerId: taskServerAfter,
      deferredShutdown,
      recovery: 'process-unknown fenced after positive container replacement proof'
    },
    evidenceRoot: plan.evidenceRoot
  };
  await writeEvidence('acceptance.json', acceptance);
  await writeEvidence('api/status.json', status);
  await writeEvidence('api/audit.json', audit);
  await writeEvidence('api/invariants.json', invariants);
  await writeEvidence('api/outboxes.json', outboxes);
  await writeEvidence('api/rolling-history.json', finalHistory);
  return acceptance;
}

async function runRetentionScenario() {
  const emptyPlan = await api('/api/v1/management/retention/plan', {
    method: 'POST',
    body: {}
  });
  if (emptyPlan.actionCount !== 0 || emptyPlan.totalBytes !== 0) {
    throw new Error(`Fresh retention plan was not empty: ${JSON.stringify(emptyPlan)}`);
  }
  const policy = await api('/api/v1/management/retention/policy');
  await api('/api/v1/management/retention/policy', {
    method: 'PUT',
    body: {
      rules: policy.rules,
      expectedVersion: policy.version,
      fullBackups: policy.fullBackups,
      archiveStorage: {
        archiveTarget: 's3',
        copyToSecondary: false,
        deleteLocalAfterVerification: false,
        deleteArchivedAfterYears: null
      }
    }
  });
  const target = await api('/api/v1/management/retention/target');
  if (target.activeTarget !== 's3' || !target.s3Configured) {
    throw new Error(`Compose S3 archive target is not configured: ${JSON.stringify(target)}`);
  }

  const ids = {
    workspace: `wsp-retention-${args.runId}`,
    project: `prj-retention-${args.runId}`,
    task: `tsk-retention-${args.runId}`,
    taskKey: 'RET-1',
    artifact: `art-retention-${args.runId}`
  };
  await api('/api/v1/workspaces', {
    method: 'POST',
    body: { name: `Retention scenario ${args.runId}`, workspaceId: ids.workspace }
  });
  await api('/api/v1/projects', {
    method: 'POST',
    body: {
      workspaceId: ids.workspace,
      name: `Retention scenario ${args.runId}`,
      taskKeyPrefix: 'RET',
      projectId: ids.project
    }
  });
  await api(`/api/v1/projects/${ids.project}/tasks`, {
    method: 'POST',
    body: {
      title: 'Disposable retention round trip',
      body: 'Seeded only inside the identity-scoped Compose fixture.',
      state: '2-ready',
      taskId: ids.task,
      taskKey: ids.taskKey
    }
  });

  const claim = await runner('/claim', { method: 'POST' });
  if (claim.task.taskId !== ids.task) {
    throw new Error(`Retention runner claimed ${claim.task.taskId}; expected ${ids.task}.`);
  }
  const content = Buffer.from('retention-round-trip\n'.repeat(64), 'utf8');
  const contentSha = createHash('sha256').update(content).digest('hex');
  await api(`/api/v1/runs/${encodeURIComponent(claim.run.runId)}/artifacts`, {
    method: 'POST',
    body: {
      artifactId: ids.artifact,
      name: 'logs/cli-output.log',
      mediaType: 'text/plain',
      contentBase64: content.toString('base64'),
      sha256: contentSha,
      idempotencyKey: `${args.runId}:retention-artifact`,
      fence: claim.lease.fence,
      runnerId: claim.lease.runnerId,
      instanceId: claim.lease.instanceId,
      leaseId: claim.lease.leaseId,
      sequence: 1
    }
  });
  await api(`/api/v1/projects/${ids.project}/tasks/${ids.task}`, {
    method: 'PUT',
    body: {
      title: null,
      body: null,
      state: '7-archive',
      expectedVersion: claim.task.version
    }
  });
  await runner('/release', { method: 'POST' });
  const before = await history(ids.project, ids.task);
  const classAHashBefore = classAInventoryHash(before);

  const archived = await api(`/api/v1/management/retention/archive/${ids.task}`, {
    method: 'POST',
    body: { stage: 2 }
  });
  if (archived.appliedActions !== 1 || archived.errors.length !== 0) {
    throw new Error(`Retention archive failed: ${JSON.stringify(archived)}`);
  }
  const archivedRead = await apiRaw(
    `/api/v1/runs/${encodeURIComponent(claim.run.runId)}/artifacts/${ids.artifact}/content`);
  if (archivedRead.status !== 409 || archivedRead.value?.code !== 'artifact-archived') {
    throw new Error(`Archived artifact read returned ${archivedRead.status}: ${JSON.stringify(archivedRead.value)}`);
  }
  const manifest = await api(`/api/v1/management/retention/archive/${ids.task}`);
  const objects = manifest.stages.flatMap(stage => stage.objects ?? []);
  if (objects.length !== 1 || objects[0].target !== 's3') {
    throw new Error(`Retention payload did not land only on S3: ${JSON.stringify(objects)}`);
  }
  const integrity = await api('/api/v1/management/retention/integrity/check', {
    method: 'POST',
    body: { sampleCount: 10 }
  });
  if (integrity.discrepancies.length !== 0 || integrity.verifiedObjects < 1) {
    throw new Error(`S3 archive integrity check was not clean: ${JSON.stringify(integrity)}`);
  }
  await api(`/api/v1/management/retention/archive/${ids.task}/restore`, {
    method: 'POST',
    body: {}
  });
  const restored = await api(
    `/api/v1/runs/${encodeURIComponent(claim.run.runId)}/artifacts/${ids.artifact}/content`);
  const restoredBytes = Buffer.from(restored.contentBase64, 'base64');
  const restoredSha = createHash('sha256').update(restoredBytes).digest('hex');
  const after = await history(ids.project, ids.task);
  const classAHashAfter = classAInventoryHash(after);
  const assertions = [
    'fresh-plan-zero',
    'single-task-stage-two-archived-to-s3',
    's3-integrity-clean',
    'archived-content-http-409',
    'restore-byte-identical',
    'class-a-inventory-unchanged'
  ];
  if (restoredSha !== contentSha || classAHashAfter !== classAHashBefore) {
    throw new Error('Retention restore changed payload bytes or the class A inventory projection.');
  }
  const result = {
    schemaVersion: 1,
    ids,
    emptyPlan,
    target,
    archiveRunId: archived.runId,
    manifest,
    integrity,
    archivedReadStatus: archivedRead.status,
    contentSha256: contentSha,
    restoredSha256: restoredSha,
    classAHashBefore,
    classAHashAfter,
    assertions
  };
  await writeEvidence('retention-scenario.json', result);
  return result;
}

function classAInventoryHash(historyValue) {
  const projection = {
    task: {
      taskId: historyValue.task.taskId,
      projectId: historyValue.task.projectId,
      taskKey: historyValue.task.taskKey,
      title: historyValue.task.title,
      body: historyValue.task.body,
      state: historyValue.task.state,
      version: historyValue.task.version,
      createdAt: historyValue.task.createdAt,
      updatedAt: historyValue.task.updatedAt
    },
    runs: historyValue.runs,
    events: historyValue.events
  };
  return createHash('sha256').update(JSON.stringify(projection)).digest('hex');
}

async function runAutonomyCanary() {
  const ids = {
    workspace: `wsp-autonomy-${args.runId}`,
    project: `prj-autonomy-${args.runId}`
  };
  await api('/api/v1/workspaces', {
    method: 'POST',
    body: {
      name: `Compose autonomy ${args.runId}`,
      workspaceId: ids.workspace
    }
  });
  await api('/api/v1/projects', {
    method: 'POST',
    body: {
      workspaceId: ids.workspace,
      name: `Compose autonomy ${args.runId}`,
      taskKeyPrefix: 'AUTO',
      projectId: ids.project
    }
  });
  const tasks = [];
  for (let index = 1; index <= 3; index++) {
    tasks.push(await api(`/api/v1/projects/${ids.project}/tasks`, {
      method: 'POST',
      body: {
        title: `Autonomy canary slot ${index}`,
        body: 'Infrastructure-only deterministic useful work.',
        state: '2-ready',
        taskId: `tsk-autonomy-${args.runId}-${index}`,
        taskKey: `AUTO-${index}`
      }
    }));
  }

  const claims = [
    await runner('/claim', { method: 'POST' }),
    await runner('/claim', { method: 'POST' })
  ];
  await waitForRunner(value =>
    value.slots?.filter(slot => slot.phase === 'working').length === 2
      && value.slots.filter(slot => slot.phase === 'working')
        .every(slot => slot.renewals >= 1), 30_000);

  await executeUnitOperation('partition', 'task-server');
  const partitionStartedMs = Date.now();
  const partitionStartedAt = new Date(partitionStartedMs).toISOString();
  const requiredWorkThroughMs =
    partitionStartedMs + autonomyPolicy.requiredDurationMs;
  const requiredWorkThroughAt = new Date(requiredWorkThroughMs).toISOString();
  const claimDuringPartition = await runnerRaw('/claim', { method: 'POST' });
  const samples = [];
  let nextProgressAt = partitionStartedMs;
  const progressIntervalMs = Math.min(
    60_000,
    Math.max(5_000, Math.floor(autonomyPolicy.requiredDurationMs / 5)));
  while (Date.now() < requiredWorkThroughMs) {
    const current = await runner('/status');
    const canarySlots = current.slots.filter(slot =>
      claims.some(claim => claim.run.runId === slot.runId));
    if (canarySlots.length !== 2
        || canarySlots.some(slot =>
          slot.phase !== 'working'
          || slot.generation.alive !== true
          || slot.generation.deathProven !== false)) {
      throw new Error(
        `Autonomy slot stopped before the configured deadline: ${JSON.stringify(canarySlots)}`);
    }
    samples.push({
      at: new Date().toISOString(),
      slots: canarySlots.map(slot => ({
        runId: slot.runId,
        workUnits: slot.workUnits,
        backlogCount: slot.backlogCount,
        stopBefore: slot.authority.stopBefore
      }))
    });
    if (Date.now() >= nextProgressAt) {
      const elapsedSeconds = Math.floor((Date.now() - partitionStartedMs) / 1000);
      progress(
        `autonomy canary ${elapsedSeconds}s/${args.autonomyDurationSeconds}s; ` +
        canarySlots.map(slot =>
          `${slot.taskKey}=work:${slot.workUnits},queued:${slot.backlogCount}`)
          .join(' '));
      await writeEvidence('autonomy-progress.json', {
        partitionStartedAt,
        requiredWorkThroughAt,
        samples
      });
      nextProgressAt += progressIntervalMs;
    }
    await delay(Math.min(10_000, Math.max(1, requiredWorkThroughMs - Date.now())));
  }

  await runner('/finish-all', { method: 'POST' });
  const partitionEndedMs = Date.now();
  const partitionEndedAt = new Date(partitionEndedMs).toISOString();
  await executeUnitOperation('heal', 'task-server');
  const recovered = await waitForRunner(value => {
    const canarySlots = value.slots?.filter(slot =>
      claims.some(claim => claim.run.runId === slot.runId)) ?? [];
    return canarySlots.length === 2
      && canarySlots.every(slot => slot.phase === 'completed');
  }, 120_000);
  const slots = recovered.slots
    .filter(slot => claims.some(claim => claim.run.runId === slot.runId))
    .map(slot => {
      const completed = recovered.timeline.findLast(item =>
        item.kind === 'completion-acknowledged'
          && item.runId === slot.runId);
      return {
        ...slot,
        reconciledAt: slot.authority.reconciledAt,
        completedAt: completed?.at ?? null
      };
    });
  const histories = [];
  for (let index = 0; index < 2; index++) {
    histories.push(await history(ids.project, tasks[index].taskId));
  }
  const unclaimedTask = await api(
    `/api/v1/projects/${ids.project}/tasks/${tasks[2].taskId}`);
  const evidence = {
    schemaVersion: 1,
    policy: autonomyPolicy,
    partitionStartedAt,
    requiredWorkThroughAt,
    partitionEndedAt,
    durationMs: partitionEndedMs - partitionStartedMs,
    claimDuringPartitionStatus: claimDuringPartition.status,
    slots,
    samples,
    histories,
    unclaimedTask
  };
  await writeEvidence('autonomy-canary-unasserted.json', evidence);
  const assertions = assertAutonomyEvidence(evidence, autonomyPolicy);
  const result = { ...evidence, assertions };
  await writeEvidence('autonomy-canary.json', result);
  await writeEvidence('api/autonomy-histories.json', histories);
  return result;
}

async function runReferenceScenario() {
  const execution = await execute([
    'node',
    path.join(suiteRoot, 'index.mjs'),
    '--scenario', 'reference-change',
    '--seed', 'compose-reference-v1',
    '--run-id', `${args.runId}-reference`,
    '--root', path.join(plan.root, 'scenarios'),
    '--server-url', plan.urls.taskServer,
    '--auth-token-file', plan.tokenFile
  ], { cwd: repoRoot, timeoutMs: 300_000 });
  const parsed = JSON.parse(execution.stdout);
  await writeEvidence('reference-task.json', parsed);
  return parsed;
}

async function captureVersions() {
  const [docker, composeVersion, server, runnerVersion, studio, nodeVersion, revision] = await Promise.all([
    execute(['docker', 'version', '--format', '{{json .}}'], { timeoutMs: 30_000 }),
    execute(['docker', 'compose', 'version', '--short'], { timeoutMs: 30_000 }),
    composeExec('task-server', ['dotnet', 'task-server.dll', '--version']),
    composeExec('agent-runner', ['dotnet', '/opt/agent-host/agent-host.dll', '--version']),
    composeExec('studio', ['caddy', 'version']),
    composeExec('fault-proxy', ['node', '--version']),
    execute(['git', 'rev-parse', 'HEAD'], { cwd: repoRoot, timeoutMs: 10_000 })
  ]);
  const images = {};
  for (const service of ['task-server', 'fault-proxy', 'agent-runner', 'studio']) {
    const imageId = (await compose(['images', '--quiet', service], 30_000)).stdout.trim();
    const inspected = await execute([
      'docker', 'image', 'inspect', imageId,
      '--format', '{{json .Id}}|{{json .RepoDigests}}|{{json .Config.Labels}}'
    ], { timeoutMs: 30_000 });
    images[service] = inspected.stdout.trim();
  }
  return {
    capturedAt: new Date().toISOString(),
    repositoryRevision: revision.stdout.trim(),
    docker: JSON.parse(docker.stdout),
    compose: composeVersion.stdout.trim(),
    components: {
      taskServer: server.stdout.trim(),
      agentRunner: runnerVersion.stdout.trim(),
      studio: studio.stdout.trim(),
      faultProxyRuntime: nodeVersion.stdout.trim()
    },
    images
  };
}

async function executeUnitOperation(operation, unit, force = false) {
  const steps = unitOperationPlan(operation, unit, { force });
  for (const step of steps) {
    if (step.kind === 'proxy') {
      const response = await fetch(`${plan.urls.faultControl}${step.route}`, { method: step.method });
      if (!response.ok) throw new Error(`${operation} ${unit} failed at ${step.route}: ${response.status}`);
    } else if (step.kind === 'api') {
      await api(step.route, { method: step.method, body: step.body });
    } else if (step.kind === 'prepare-shutdown') {
      const prepared = await api(step.route, { method: step.method, body: step.body });
      if (!prepared.safeToStop && !step.allowUnsafe) {
        throw new Error(`Task Server refused bounded shutdown: ${prepared.message}`);
      }
    } else if (step.kind === 'wait-ready') {
      await waitForJson(`${plan.urls.taskServer}/readyz`, value => value.status === 'ready', 60_000);
    } else if (step.kind === 'wait-unit') {
      if (step.unit === 'runner') {
        await waitForJson(`${plan.urls.runnerControl}/healthz`, value => value.status === 'ready', 60_000);
      } else if (step.unit === 'studio') {
        await waitForText(`${plan.urls.studio}/`, text => text.includes('<app-root'), 60_000);
      }
    } else if (step.kind === 'compose') {
      await compose(step.args, 180_000);
    }
  }
}

async function initializeRunRoot() {
  if (await exists(plan.root)) {
    throw new Error(`Run id '${args.runId}' already has a workspace or evidence. Use a new run id.`);
  }
  await mkdir(plan.evidenceRoot, { recursive: true });
  authToken = randomBytes(36).toString('base64url');
  const revision = (await execute(['git', 'rev-parse', 'HEAD'], { cwd: repoRoot, timeoutMs: 10_000 })).stdout.trim();
  const environment = [
    `REMOTE_HARNESS_PROJECT=${plan.project}`,
    `REMOTE_HARNESS_RUN_ID=${plan.runId}`,
    `REMOTE_HARNESS_IMAGE_TAG=local`,
    `REMOTE_HARNESS_REVISION=${revision}`,
    `REMOTE_HARNESS_AUTH_TOKEN=${authToken}`,
    `REMOTE_HARNESS_S3_CREDENTIALS_FILE=${plan.archiveCredentialFile}`,
    `REMOTE_HARNESS_TASK_SERVER_PORT=${plan.ports.taskServer}`,
    `REMOTE_HARNESS_STUDIO_PORT=${plan.ports.studio}`,
    `REMOTE_HARNESS_FAULT_CONTROL_PORT=${plan.ports.faultControl}`,
    `REMOTE_HARNESS_RUNNER_CONTROL_PORT=${plan.ports.runnerControl}`
  ].join('\n') + '\n';
  await writeFile(plan.environmentFile, environment, { mode: 0o600 });
  await writeFile(plan.tokenFile, `${authToken}\n`, { mode: 0o600 });
  await writeFile(plan.archiveCredentialFile,
    `${JSON.stringify({ accessKey: 'minioadmin', secretKey: 'minioadmin' })}\n`, { mode: 0o600 });
  await chmod(plan.environmentFile, 0o600);
  await chmod(plan.tokenFile, 0o600);
  await chmod(plan.archiveCredentialFile, 0o600);
  await writeEvidence('resource-plan.json', {
    ...plan,
    environmentFile: path.relative(plan.repoRoot, plan.environmentFile),
    tokenFile: '[ephemeral credential removed during teardown]',
    archiveCredentialFile: '[ephemeral credential removed during teardown]'
  });
  await copyFile(plan.composeFile, path.join(plan.evidenceRoot, 'compose-source.yaml'));
}

async function loadExistingEnvironment() {
  const contents = await readFile(plan.environmentFile, 'utf8');
  const environment = Object.fromEntries(contents.trim().split(/\r?\n/)
    .map(line => line.split(/=(.*)/s).slice(0, 2)));
  if (environment.REMOTE_HARNESS_PROJECT !== plan.project
      || environment.REMOTE_HARNESS_RUN_ID !== plan.runId) {
    throw new Error('Existing harness environment identity does not match the requested run.');
  }
  authToken = environment.REMOTE_HARNESS_AUTH_TOKEN;
  if (!authToken) throw new Error('Existing harness environment has no authentication token.');
}

async function validateCompose() {
  await compose(['config', '--quiet'], 30_000);
  const services = (await compose(['config', '--services'], 30_000)).stdout.trim().split(/\r?\n/).sort();
  const expected = ['agent-runner', 'fault-proxy', 'minio', 'minio-init', 'studio', 'task-server'];
  if (JSON.stringify(services) !== JSON.stringify(expected)) {
    throw new Error(`Compose profile resolved unexpected services: ${services.join(', ')}`);
  }
}

async function collectEvidence(label) {
  if (!await exists(plan.environmentFile)) return;
  await mkdir(plan.evidenceRoot, { recursive: true });
  const ps = await composeAllowFailure(['ps', '--all', '--format', 'json'], 30_000);
  await writeTextEvidence(`compose-ps-${label}.json`, ps.stdout || ps.stderr);
  for (const service of ['task-server', 'minio', 'minio-init', 'fault-proxy', 'agent-runner', 'studio']) {
    const logs = await composeAllowFailure(['logs', '--no-color', '--timestamps', service], 30_000);
    await writeTextEvidence(`logs/${service}.log`, redact(logs.stdout + logs.stderr, [authToken]));
    const id = (await composeAllowFailure(['ps', '--all', '--quiet', service], 15_000)).stdout.trim();
    if (id) {
      const inspected = await executeAllowFailure(['docker', 'inspect', id], { timeoutMs: 30_000 });
      await writeTextEvidence(
        `inspect/${service}.json`,
        sanitizeEnvironmentValues(inspected.stdout + inspected.stderr, [authToken]));
    }
  }
  for (const [file, route] of Object.entries({
    status: '/api/v1/management/status',
    audit: '/api/v1/management/audit',
    invariants: '/api/v1/management/invariants',
    outboxes: '/api/v1/management/outboxes'
  })) {
    const response = await apiRaw(route);
    if (response.status === 200) await writeEvidence(`api/${file}.json`, response.value);
  }
}

async function teardown() {
  if (!await exists(plan.environmentFile)) return;
  await assertOwnedResources();
  await composeAllowFailure([
    'down', '--volumes', '--remove-orphans', '--timeout', '20'
  ], 120_000);
  for (const image of plan.resources.images) {
    await executeAllowFailure(['docker', 'image', 'rm', image], { timeoutMs: 30_000 });
  }
  const residues = await identityResidues();
  if (residues.containers.length || residues.volumes.length || residues.networks.length) {
    throw new Error(`Identity-scoped teardown left resources: ${JSON.stringify(residues)}`);
  }
  await rm(plan.environmentFile, { force: true });
  await rm(plan.tokenFile, { force: true });
  await rm(plan.archiveCredentialFile, { force: true });
  await writeEvidence('teardown.json', {
    completedAt: new Date().toISOString(),
    project: plan.project,
    identityLabel: plan.identityLabel,
    residues
  });
}

async function assertOwnedResources() {
  const ids = (await executeAllowFailure([
    'docker', 'ps', '--all', '--quiet',
    '--filter', `label=com.docker.compose.project=${plan.project}`
  ], { timeoutMs: 30_000 })).stdout.trim().split(/\r?\n/).filter(Boolean);
  for (const id of ids) {
    const label = (await execute([
      'docker', 'inspect', id,
      '--format', '{{index .Config.Labels "com.agentstudio.remote-harness.identity"}}'
    ], { timeoutMs: 30_000 })).stdout.trim();
    if (label !== plan.project) {
      throw new Error(`Refusing teardown: container ${id} lacks harness identity ${plan.project}.`);
    }
  }
  for (const volume of plan.resources.volumes) {
    await assertDockerLabel(
      ['docker', 'volume', 'inspect', volume, '--format',
        '{{index .Labels "com.agentstudio.remote-harness.identity"}}'],
      `volume ${volume}`
    );
  }
  await assertDockerLabel(
    ['docker', 'network', 'inspect', plan.resources.network, '--format',
      '{{index .Labels "com.agentstudio.remote-harness.identity"}}'],
    `network ${plan.resources.network}`
  );
  for (const image of plan.resources.images) {
    await assertDockerLabel(
      ['docker', 'image', 'inspect', image, '--format',
        '{{index .Config.Labels "com.agentstudio.remote-harness.identity"}}'],
      `image ${image}`
    );
  }
}

async function assertDockerLabel(command, description) {
  const inspected = await executeAllowFailure(command, { timeoutMs: 30_000 });
  if (inspected.code !== 0) return;
  if (inspected.stdout.trim() !== plan.project) {
    throw new Error(`Refusing teardown: ${description} lacks harness identity ${plan.project}.`);
  }
}

async function ensureNoIdentityResources() {
  const residues = await identityResidues();
  if (residues.containers.length || residues.volumes.length || residues.networks.length) {
    throw new Error(`Harness identity already owns Docker resources: ${JSON.stringify(residues)}`);
  }
  const collisions = await exactNameCollisions();
  if (collisions.length) {
    throw new Error(`Refusing to reuse explicit Docker resource names: ${collisions.join(', ')}`);
  }
}

async function exactNameCollisions() {
  const collisions = [];
  for (const container of plan.resources.containers) {
    const found = await executeAllowFailure([
      'docker', 'ps', '--all', '--quiet', '--filter', `name=^/${container}$`
    ], { timeoutMs: 30_000 });
    if (found.stdout.trim()) collisions.push(`container:${container}`);
  }
  for (const volume of plan.resources.volumes) {
    const found = await executeAllowFailure(['docker', 'volume', 'inspect', volume], { timeoutMs: 30_000 });
    if (found.code === 0) collisions.push(`volume:${volume}`);
  }
  const network = await executeAllowFailure(
    ['docker', 'network', 'inspect', plan.resources.network],
    { timeoutMs: 30_000 }
  );
  if (network.code === 0) collisions.push(`network:${plan.resources.network}`);
  for (const image of plan.resources.images) {
    const found = await executeAllowFailure(['docker', 'image', 'inspect', image], { timeoutMs: 30_000 });
    if (found.code === 0) collisions.push(`image:${image}`);
  }
  return collisions;
}

async function identityResidues() {
  const label = `com.agentstudio.remote-harness.identity=${plan.project}`;
  const [containers, volumes, networks] = await Promise.all([
    executeAllowFailure(['docker', 'ps', '--all', '--quiet', '--filter', `label=${label}`], { timeoutMs: 30_000 }),
    executeAllowFailure(['docker', 'volume', 'ls', '--quiet', '--filter', `label=${label}`], { timeoutMs: 30_000 }),
    executeAllowFailure(['docker', 'network', 'ls', '--quiet', '--filter', `label=${label}`], { timeoutMs: 30_000 })
  ]);
  return {
    containers: lines(containers.stdout),
    volumes: lines(volumes.stdout),
    networks: lines(networks.stdout)
  };
}

async function hasIdentityContainers() {
  return (await identityResidues()).containers.length > 0;
}

async function api(route, options = {}) {
  const response = await apiRaw(route, options);
  if (response.status < 200 || response.status >= 300) {
    throw new Error(`${options.method ?? 'GET'} ${route} failed (${response.status}): ${JSON.stringify(response.value)}`);
  }
  return response.value;
}

async function apiRaw(route, { method = 'GET', body } = {}) {
  try {
    const response = await fetch(`${plan.urls.taskServer}${route}`, {
      method,
      headers: {
        Authorization: `Bearer ${authToken}`,
        'Content-Type': 'application/json',
        'X-Actor-Id': `remote-compose-harness:${args.runId}`,
        'X-Client-Id': `remote-compose-harness:${args.runId}`,
        'X-Task-Protocol-Version': '2',
        'X-Task-Client-Version': 'remote-compose-harness/1'
      },
      body: body === undefined ? undefined : JSON.stringify(body)
    });
    const text = await response.text();
    let value;
    try {
      value = text ? JSON.parse(text) : null;
    } catch {
      value = { raw: text };
    }
    return { status: response.status, value };
  } catch (error) {
    return { status: 0, value: { error: String(error?.message ?? error) } };
  }
}

async function runner(route, { method = 'GET' } = {}) {
  const response = await runnerRaw(route, { method });
  if (response.status < 200 || response.status >= 300) {
    throw new Error(`Runner control ${route} failed (${response.status}): ${JSON.stringify(response.value)}`);
  }
  return response.value;
}

async function runnerRaw(route, { method = 'GET' } = {}) {
  const response = await fetch(`${plan.urls.runnerControl}${route}`, { method });
  const value = await response.json();
  return { status: response.status, value };
}

async function proxy(link, action) {
  const response = await fetch(`${plan.urls.faultControl}/links/${link}/${action}`, { method: 'POST' });
  if (!response.ok) throw new Error(`Fault proxy ${link}/${action} failed (${response.status}).`);
  return await response.json();
}

async function history(projectId, taskId) {
  return await api(`/api/v1/projects/${projectId}/tasks/${taskId}/history`);
}

async function historyViaStudio(projectId, taskId) {
  const response = await fetch(`${plan.urls.studio}/api/v1/projects/${projectId}/tasks/${taskId}/history`, {
    headers: {
      'X-Actor-Id': `remote-compose-harness:${args.runId}`,
      'X-Client-Id': `remote-compose-harness:${args.runId}`
    }
  });
  if (!response.ok) throw new Error(`Studio history replay failed (${response.status}).`);
  return await response.json();
}

async function waitForRunner(predicate, timeoutMs) {
  return await waitForJson(`${plan.urls.runnerControl}/status`, predicate, timeoutMs);
}

async function waitForJson(url, predicate, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  let last = 'no response';
  while (Date.now() < deadline) {
    try {
      const response = await fetch(url);
      const text = await response.text();
      last = `${response.status}: ${text}`;
      if (response.ok) {
        const value = JSON.parse(text);
        if (predicate(value)) return value;
      }
    } catch (error) {
      last = String(error?.message ?? error);
    }
    await delay(250);
  }
  throw new Error(`Timed out waiting for ${url}: ${last}`);
}

async function waitForText(url, predicate, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  let last = 'no response';
  while (Date.now() < deadline) {
    try {
      const response = await fetch(url);
      const text = await response.text();
      last = `${response.status}: ${text.slice(0, 200)}`;
      if (response.ok && predicate(text)) return text;
    } catch (error) {
      last = String(error?.message ?? error);
    }
    await delay(250);
  }
  throw new Error(`Timed out waiting for ${url}: ${last}`);
}

async function containerId(service) {
  const id = (await compose(['ps', '--quiet', service], 30_000)).stdout.trim();
  if (!id) throw new Error(`Compose service ${service} has no running container.`);
  return id;
}

function assertChangedContainer(label, before, after) {
  if (!before || !after || before === after) throw new Error(`${label} replacement did not change container identity.`);
}

async function compose(args, timeoutMs) {
  return await execute(composeCommand(plan, ...args), { cwd: repoRoot, timeoutMs });
}

async function composeAllowFailure(args, timeoutMs) {
  return await executeAllowFailure(composeCommand(plan, ...args), { cwd: repoRoot, timeoutMs });
}

async function composeExec(service, command) {
  return await compose(['exec', '--no-TTY', service, ...command], 30_000);
}

async function execute(command, { cwd = repoRoot, timeoutMs = 60_000 } = {}) {
  const result = await executeAllowFailure(command, { cwd, timeoutMs });
  if (result.code !== 0) {
    throw new Error(`${command.join(' ')} failed (${result.code}): ${result.stderr || result.stdout}`);
  }
  return result;
}

async function executeAllowFailure(command, { cwd = repoRoot, timeoutMs = 60_000 } = {}) {
  return await new Promise((resolve, reject) => {
    const child = spawn(command[0], command.slice(1), {
      cwd,
      env: process.env,
      stdio: ['ignore', 'pipe', 'pipe']
    });
    let stdout = '';
    let stderr = '';
    let timedOut = false;
    const timer = setTimeout(() => {
      timedOut = true;
      child.kill('SIGTERM');
      setTimeout(() => child.kill('SIGKILL'), 5000).unref();
    }, timeoutMs);
    child.stdout.on('data', chunk => { stdout += chunk; });
    child.stderr.on('data', chunk => { stderr += chunk; });
    child.on('error', reject);
    child.on('close', code => {
      clearTimeout(timer);
      resolve({ code: timedOut ? 124 : (code ?? 1), stdout, stderr });
    });
  });
}

async function writeEvidence(relative, value) {
  const file = path.join(plan.evidenceRoot, relative);
  await mkdir(path.dirname(file), { recursive: true });
  await writeFile(file, `${JSON.stringify(value, null, 2)}\n`);
}

async function writeTextEvidence(relative, value) {
  const file = path.join(plan.evidenceRoot, relative);
  await mkdir(path.dirname(file), { recursive: true });
  await writeFile(file, value);
}

async function writeFailure(error) {
  await mkdir(plan.evidenceRoot, { recursive: true });
  await writeEvidence('failure.json', {
    failedAt: new Date().toISOString(),
    message: error?.message ?? String(error),
    stack: redact(error?.stack ?? '', [authToken])
  });
  if (stackStarted) await collectEvidence('failure');
}

async function exists(file) {
  try {
    await stat(file);
    return true;
  } catch (error) {
    if (error?.code === 'ENOENT') return false;
    throw error;
  }
}

function lines(value) {
  return value.trim().split(/\r?\n/).filter(Boolean);
}

function delay(milliseconds) {
  return new Promise(resolve => setTimeout(resolve, milliseconds));
}

function progress(message) {
  process.stderr.write(`[remote-compose-harness] ${message}\n`);
}

function parseArgs(values) {
  const result = {
    command: values[0] ?? '',
    runId: '',
    operation: null,
    unit: null,
    force: false,
    keep: false,
    machineBound: false,
    autonomyDurationSeconds: environmentPositiveInt(
      'REMOTE_TEST_AUTONOMY_SECONDS',
      defaultAutonomyDurationSeconds),
    ports: { ...defaultPorts }
  };
  let index = 1;
  if (result.command === 'control') {
    result.operation = values[index++];
    result.unit = values[index++];
  }
  for (; index < values.length; index++) {
    const key = values[index];
    if (key === '--force') result.force = true;
    else if (key === '--keep') result.keep = true;
    else if (key === '--machine-bound') result.machineBound = true;
    else if (key === '--run-id') result.runId = values[++index];
    else if (key === '--autonomy-duration-seconds') {
      result.autonomyDurationSeconds = Number(values[++index]);
    }
    else if (key === '--task-server-port') result.ports.taskServer = Number(values[++index]);
    else if (key === '--studio-port') result.ports.studio = Number(values[++index]);
    else if (key === '--fault-control-port') result.ports.faultControl = Number(values[++index]);
    else if (key === '--runner-control-port') result.ports.runnerControl = Number(values[++index]);
    else throw new Error(`Unknown argument: ${key}`);
  }
  if (!['inspect', 'run', 'up', 'down', 'control'].includes(result.command) || !result.runId) {
    throw new Error(
      'Usage: compose-harness.mjs inspect|run|up|down --run-id ID [--autonomy-duration-seconds N] [--machine-bound], or compose-harness.mjs control stop|restart|replace|partition|heal studio|task-server|runner --run-id ID'
    );
  }
  if (result.command === 'control' && (!result.operation || !result.unit)) {
    throw new Error('Control requires an operation and unit.');
  }
  return result;
}

function environmentPositiveInt(name, fallback) {
  const raw = process.env[name]?.trim();
  if (!raw) return fallback;
  const value = Number(raw);
  if (!Number.isInteger(value) || value <= 0) {
    throw new Error(`${name} must be a positive integer.`);
  }
  return value;
}
