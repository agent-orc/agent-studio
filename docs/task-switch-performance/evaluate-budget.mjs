#!/usr/bin/env node
import { readFile, writeFile } from 'node:fs/promises';
import { basename } from 'node:path';

export function percentile(values, fraction) {
  if (!values.length) return null;
  const sorted = [...values].sort((a, b) => a - b);
  const rank = fraction * (sorted.length - 1);
  const low = Math.floor(rank);
  return +(sorted[low] + (sorted[Math.ceil(rank)] - sorted[low]) * (rank - low)).toFixed(3);
}

const requiredStages = ['task-core', 'core-index', 'core-runtime', 'core-serialize'];
export const requiredCohorts = ['board-click', 'pager-keyboard', 'back-forward', 'deep-link',
  'long-document', 'cached-core', 'cold-client', 'dirty-unrelated', 'own-task-mutation',
  'git-unavailable', 'hung-refresh'].flatMap(name => [`light/${name}`, `dark/${name}`]);

export function mergeCaptures(normal, isolated) {
  const mergeFailures = [];
  if (normal.profile?.name !== isolated.profile?.name)
    mergeFailures.push('normal and isolated workstation profile names differ');
  if (normal.profile?.source !== isolated.profile?.source)
    mergeFailures.push('normal and isolated source declarations differ');
  if (!normal.profile?.backendVersion?.commit || !isolated.profile?.backendVersion?.commit)
    mergeFailures.push('normal or isolated backend revision is missing');
  if (normal.profile?.backendVersion?.commit !== isolated.profile?.backendVersion?.commit)
    mergeFailures.push('normal and isolated backend revisions differ');
  return { ...normal, cohorts: { ...normal.cohorts, ...isolated.cohorts },
    pageErrors: [...(normal.pageErrors ?? []), ...(isolated.pageErrors ?? [])],
    requestErrors: [...(normal.requestErrors ?? []), ...(isolated.requestErrors ?? [])],
    screenshots: [...(normal.screenshots ?? []), ...(isolated.screenshots ?? [])],
    maxConcurrentCoreRequests: Math.max(normal.maxConcurrentCoreRequests ?? 0,
      isolated.maxConcurrentCoreRequests ?? 0), mergeFailures };
}

export function evaluateBudget(capture) {
  const failures = [];
  failures.push(...(capture.mergeFailures ?? []));
  if (capture.profile?.kind !== 'designated-workstation') failures.push('profile is not the designated workstation');
  if (!capture.profile?.source || capture.profile.source === 'unknown') failures.push('tested source revision is missing');
  if (capture.profile?.backendReadiness !== 'ready-before-capture') failures.push('backend was not ready before capture');
  if ((capture.pageErrors ?? []).length) failures.push(`${capture.pageErrors.length} page errors`);
  if ((capture.maxConcurrentCoreRequests ?? 0) > 2) failures.push('more than two concurrent core requests');
  const allHandlers = [];
  for (const name of requiredCohorts)
    if (!Object.hasOwn(capture.cohorts ?? {}, name)) failures.push(`missing cohort ${name}`);
  const cohorts = {};
  for (const [name, population] of Object.entries(capture.cohorts ?? {})) {
    const samples = population.samples ?? [];
    const reasons = [];
    if ((population.warmups ?? 0) < 5) reasons.push('fewer than five declared warmups');
    if ((population.warmupSamples ?? []).length !== population.warmups)
      reasons.push('warmup sample count does not match declaration');
    if ((population.warmupSamples ?? []).some(sample => sample.outcome !== 'ok'))
      reasons.push('warmup switch failed');
    if (/\/(dirty-unrelated|own-task-mutation)$/.test(name)
      && (population.mutationCount ?? 0) < samples.length + population.warmups)
      reasons.push('isolated mutation was not applied for every switch');
    if (/\/(git-unavailable|hung-refresh)$/.test(name)
      && (population.faultHits ?? 0) < samples.length + population.warmups)
      reasons.push('isolated Git fault was not exercised for every switch');
    if (/\/(dirty-unrelated|own-task-mutation|git-unavailable|hung-refresh)$/.test(name)
      && population.fixture !== 'isolated dev-backend task repository')
      reasons.push('fault cohort lacks isolated fixture provenance');
    if (samples.length < 100) reasons.push('fewer than 100 measured switches');
    if (/\/(dirty-unrelated|own-task-mutation|git-unavailable|hung-refresh)$/.test(name)) {
      for (const [taskClass, lane, input] of [
        ['active', '2-ready', 'keydown'], ['review', '5-human-review', 'keydown'],
        ['archived', '7-archive', 'click'],
      ]) {
        if (!samples.some(sample => sample.sampleClass === taskClass
          && sample.expectedLane === lane && sample.input === input && sample.classVerified === true))
          reasons.push(`missing verified ${taskClass} task switch`);
      }
    }
    const budget = population.cached ? 50 : 100;
    for (const [index, sample] of samples.entries()) {
      const label = `${name} sample ${index + 1}`;
      if (sample.outcome !== 'ok' || !Number.isFinite(sample.paintMs)) reasons.push(`${label}: timeout or failed paint`);
      if (!Number.isFinite(sample.inputAtEpochMs)) reasons.push(`${label}: missing actual input timestamp`);
      if (sample.coreFacts !== true) reasons.push(`${label}: incomplete core facts`);
      if (sample.groupedReads !== 0) reasons.push(`${label}: selection caused grouped read or counter missing`);
      for (const request of sample.coreRequests ?? []) {
        if (request.status !== 200 && request.status !== 304) reasons.push(`${label}: core HTTP ${request.status}`);
        if (request.status === 200 && !['ready', 'stale'].includes(request.coreState))
          reasons.push(`${label}: warming or missing core state`);
        if (request.status === 200 && !request.coreVersion)
          reasons.push(`${label}: missing core generation`);
        if (!Number.isFinite(request.bytes) || request.bytes > 16 * 1024) reasons.push(`${label}: core payload missing or above 16 KiB`);
        if (request.gitSpawns !== 0) reasons.push(`${label}: Git spawn counter missing or nonzero`);
        if (request.workspaceScans !== 0) reasons.push(`${label}: workspace scan counter missing or nonzero`);
        if (!/^[0-9a-f]{32}$/i.test(request.requestId ?? '')) reasons.push(`${label}: missing correlated request ID`);
        if (!/^[0-9a-f]{32}$/i.test(request.responseSwitchId ?? '')
            || request.responseSwitchId !== sample.switchId)
          reasons.push(`${label}: mismatched switch correlation ID`);
        for (const stage of requiredStages)
          if (!Number.isFinite(request.serverTiming?.[stage])) reasons.push(`${label}: missing ${stage} span`);
      }
      if (name.endsWith('/cold-client') && (sample.coreRequests ?? []).length === 0)
        reasons.push(`${label}: cold client has no correlated core request`);
    }
    const values = samples.filter(s => s.outcome === 'ok' && Number.isFinite(s.paintMs)).map(s => s.paintMs);
    const handlers = samples.flatMap(s => s.coreRequests ?? []).map(r => r.serverTiming?.['task-core']).filter(Number.isFinite);
    const requests = samples.flatMap(s => s.coreRequests ?? []);
    const stageNames = new Set(requests.flatMap(request => Object.keys(request.serverTiming ?? {})));
    const stageStats = Object.fromEntries([...stageNames].map(stage => {
      const values = requests.map(request => request.serverTiming?.[stage]).filter(Number.isFinite);
      return [stage, { count: values.length, p50Ms: percentile(values, .5), p95Ms: percentile(values, .95) }];
    }));
    allHandlers.push(...handlers);
    const p50Ms = percentile(values, .5);
    const p95Ms = percentile(values, .95);
    const handlerP95Ms = percentile(handlers, .95);
    if (p95Ms === null || p95Ms > budget) reasons.push(`paint p95 ${p95Ms} ms exceeds ${budget} ms`);
    if (handlerP95Ms !== null && handlerP95Ms > 30)
      reasons.push(`core handler p95 ${handlerP95Ms} ms exceeds 30 ms`);
    cohorts[name] = { count: samples.length, successful: values.length, warmups: population.warmups,
      p50Ms, p95Ms, handlerP95Ms, budgetMs: budget,
      maxCoreBytes: requests.length ? Math.max(...requests.map(request => request.bytes ?? 0)) : null,
      gitSpawns: requests.every(request => Number.isFinite(request.gitSpawns))
        ? requests.reduce((sum, request) => sum + request.gitSpawns, 0) : null,
      stageStats, failures: reasons };
    failures.push(...reasons);
  }
  if (!Object.keys(cohorts).length) failures.push('no cohorts');
  const coreHandlerP95Ms = percentile(allHandlers, .95);
  if (coreHandlerP95Ms === null) failures.push('no core handler samples');
  else if (coreHandlerP95Ms > 30) failures.push(`global core handler p95 ${coreHandlerP95Ms} ms exceeds 30 ms`);
  return { passed: failures.length === 0, failures, coreHandlerP95Ms, cohorts };
}

if (basename(process.argv[1] ?? '') === 'evaluate-budget.mjs') {
  const [input, output, flag, isolatedPath] = process.argv.slice(2);
  if (!input) throw Error('Usage: node evaluate-budget.mjs capture.json [summary.json] [--isolated isolated.json]');
  let capture = JSON.parse(await readFile(input, 'utf8'));
  if (flag === '--isolated' && isolatedPath)
    capture = mergeCaptures(capture, JSON.parse(await readFile(isolatedPath, 'utf8')));
  else if (flag) throw Error('Expected --isolated and an isolated capture path');
  const result = evaluateBudget(capture);
  const serialized = JSON.stringify(result, null, 2) + '\n';
  if (output) await writeFile(output, serialized);
  else process.stdout.write(serialized);
  if (!result.passed) process.exitCode = 1;
}
