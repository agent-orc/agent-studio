import { test } from 'node:test';
import { strict as assert } from 'node:assert';
import { evaluateBudget, mergeCaptures, percentile, requiredCohorts } from './evaluate-budget.mjs';

function capture() {
  const request = () => ({ status: 200, coreState: 'ready', coreVersion: '1', bytes: 4096, gitSpawns: 0, workspaceScans: 0,
    requestId: 'a'.repeat(32), responseSwitchId: 'b'.repeat(32),
    serverTiming: { 'task-core': 10, 'core-index': 1, 'core-runtime': 1, 'core-serialize': 1 } });
  return { profile: { name: 'workstation-a', source: 'candidate-a', backendVersion: { commit: 'candidate-a' },
    kind: 'designated-workstation', backendReadiness: 'ready-before-capture' },
    pageErrors: [], maxConcurrentCoreRequests: 1, cohorts: Object.fromEntries(requiredCohorts.map(name => [name,
      { warmups: 5, warmupSamples: Array.from({ length: 5 }, () => ({ outcome: 'ok' })),
        fixture: 'isolated dev-backend task repository', mutationCount: 105, faultHits: 105,
        cached: name.endsWith('/cached-core'), samples: Array.from({ length: 100 }, () => ({
        outcome: 'ok', switchId: 'b'.repeat(32), paintMs: 40, inputAtEpochMs: 1_780_000_000_000,
        coreFacts: true, groupedReads: 0, coreRequests: [request()],
      })) }])) };
}

test('uses the measurement protocol interpolation and accepts a complete population', () => {
  assert.equal(percentile([1, 2, 3, 4], .5), 2.5);
  const result = evaluateBudget(capture());
  assert.equal(result.passed, true);
  assert.deepEqual(result.cohorts['light/board-click'].stageStats['core-index'],
    { count: 100, p50Ms: 1, p95Ms: 1 });
});

test('isolated 150 ms core delay fails the paint budget', () => {
  const subject = capture();
  subject.cohorts['light/board-click'].samples.forEach(sample => { sample.paintMs += 150; sample.coreRequests[0].serverTiming['task-core'] += 150; });
  assert.equal(evaluateBudget(subject).passed, false);
  assert.match(evaluateBudget(subject).failures.join(' '), /paint p95/);
});

test('isolated request-path Git invocation and selection board refetch each fail', () => {
  const git = capture();
  git.cohorts['light/board-click'].samples[0].coreRequests[0].gitSpawns = 1;
  assert.match(evaluateBudget(git).failures.join(' '), /Git spawn/);
  const board = capture();
  board.cohorts['light/board-click'].samples[0].groupedReads = 1;
  assert.match(evaluateBudget(board).failures.join(' '), /grouped read/);
});

test('timeouts, warming, missing spans, counters, and page errors remain failures', () => {
  const subject = capture();
  const samples = subject.cohorts['light/board-click'].samples;
  samples[0].outcome = 'timeout';
  samples[1].coreRequests[0].status = 202;
  delete samples[2].coreRequests[0].serverTiming['core-index'];
  delete samples[3].coreRequests[0].gitSpawns;
  subject.pageErrors.push('uncaught');
  const result = evaluateBudget(subject);
  assert.equal(result.passed, false);
  assert.equal(result.cohorts['light/board-click'].count, 100);
  assert.equal(result.cohorts['light/board-click'].successful, 99);
  assert.match(result.failures.join(' '), /timeout.*HTTP 202.*missing core-index.*spawn counter.*page errors|page errors.*timeout.*HTTP 202.*missing core-index.*spawn counter/);
});

test('a missing required theme or failure cohort is not a passing gate', () => {
  const subject = capture();
  delete subject.cohorts['dark/hung-refresh'];
  assert.match(evaluateBudget(subject).failures.join(' '), /missing cohort dark\/hung-refresh/);
});

test('a core response from another switch cannot satisfy the selected sample', () => {
  const subject = capture();
  subject.cohorts['light/board-click'].samples[0].coreRequests[0].responseSwitchId = 'c'.repeat(32);
  assert.match(evaluateBudget(subject).failures.join(' '), /mismatched switch correlation ID/);
});

test('normal and isolated populations merge only at the same revision', () => {
  const full = capture();
  const normal = { ...full, cohorts: Object.fromEntries(Object.entries(full.cohorts)
    .filter(([name]) => !/(dirty-unrelated|own-task-mutation|git-unavailable|hung-refresh)$/.test(name))) };
  const isolated = { ...full, cohorts: Object.fromEntries(Object.entries(full.cohorts)
    .filter(([name]) => /(dirty-unrelated|own-task-mutation|git-unavailable|hung-refresh)$/.test(name))) };
  assert.equal(evaluateBudget(mergeCaptures(normal, isolated)).passed, true);
  isolated.profile = { ...isolated.profile, backendVersion: { commit: 'different' } };
  assert.match(evaluateBudget(mergeCaptures(normal, isolated)).failures.join(' '), /revisions differ/);
});
