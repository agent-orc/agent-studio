#!/usr/bin/env node
import { spawnSync } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import { resolve } from 'node:path';

// The existing Playwright project owns browser lifecycle and artifacts. A
// missing workstation declaration fails before any timing is interpreted.
if (!process.env.TASK_SWITCH_PROFILE || process.env.TASK_SWITCH_PROFILE_KIND !== 'designated-workstation'
    || !process.env.TASK_SWITCH_SOURCE || !process.env.TASK_CORE_BENCH_FIXTURE) {
  process.stderr.write('Task-switch budget requires TASK_SWITCH_PROFILE, TASK_SWITCH_SOURCE, TASK_CORE_BENCH_FIXTURE and TASK_SWITCH_PROFILE_KIND=designated-workstation.\n');
  process.exit(2);
}
const playwrightCli = resolve('node_modules/@playwright/test/cli.js');
const outputDir = resolve(process.env.JOB_RESULTS_DIR ?? 'test-results/task-switch-budget');
mkdirSync(outputDir, { recursive: true });
const normalPath = resolve(outputDir, 'task-switch-budget.json');
const isolatedPath = resolve(outputDir, 'task-switch-isolated.json');
const coreBenchmark = spawnSync('dotnet',
  ['test', '../backend.Tests/OrchestratorApi.Tests.csproj', '--no-build',
    '--filter', 'FullyQualifiedName~TaskCoreEndpointTests.WarmCore_HandlerP95_OnProductionShapedSnapshot',
    '--nologo'],
  { stdio: 'inherit', env: { ...process.env,
    TASK_CORE_BENCH_REPORT: resolve(outputDir, 'task-core-benchmark.json') } });
if (coreBenchmark.status !== 0) process.exit(coreBenchmark.status ?? 1);
const common = { ...process.env, TASK_SWITCH_BUDGET: '1', TASK_SWITCH_DEFER_EVAL: '1',
  JOB_RESULTS_DIR: outputDir };
for (const [spec, target, extra] of [
  ['e2e/perf/task-switch-budget.spec.ts', process.env.TASK_SWITCH_NORMAL_TARGET ?? 'stable',
    { TASK_SWITCH_OUTPUT: normalPath }],
  ['e2e/perf/task-switch-budget-isolated.spec.ts', 'dev', {}],
]) {
  const run = spawnSync(process.execPath,
    [playwrightCli, 'test', spec, '--project=chromium', '--workers=1', '--reporter=list'],
    { stdio: 'inherit', env: { ...common, ...extra, PW_TARGET: target } });
  if (run.status !== 0) process.exit(run.status ?? 1);
}
const verdict = spawnSync(process.execPath,
  [resolve('../docs/task-switch-performance/evaluate-budget.mjs'), normalPath,
    resolve(outputDir, 'task-switch-budget-summary.json'), '--isolated', isolatedPath],
  { stdio: 'inherit' });
process.exit(verdict.status ?? 1);
