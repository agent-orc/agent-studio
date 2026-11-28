import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { access, mkdir, mkdtemp, readFile, rm, symlink, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { waitForCompletion } from './completion.mjs';

function observe(states) {
  let polls = 0, time = 0;
  const clock = { now: () => new Date(time), sleep: async ms => { time += ms; } };
  const lanes = [];
  const readCard = () => ({ key: 'CC-1', state: states[Math.min(polls++, states.length - 1)] });
  return { result: waitForCompletion(readCard, lane => lanes.push(lane), { clock, intervalMs: 10, timeoutMs: 50 }), lanes };
}

test('completion waits through automatic and human review', async () => {
  const states = ['2-ready', '3-progress', '4-auto-review', '5-human-review', '6-completed'];
  const { result, lanes } = observe(states);
  assert.equal((await result).state, '6-completed');
  assert.deepEqual(lanes, states);
});

test('a parked card reports its lane and a stalled review times out', async () => {
  await assert.rejects(observe(['5e-escalated']).result, /CC-1.*5e-escalated/);
  const { result, lanes } = observe(['4-auto-review']);
  await assert.rejects(result, /Timed out/);
  assert.deepEqual(lanes, ['4-auto-review']);
});

test('a failed fixture restart replaces stale evidence and preserves the other scenario', async t => {
  const dir = await mkdtemp(path.join(os.tmpdir(), 'scenario-rerun-'));
  t.after(() => rm(dir, { recursive: true, force: true }));
  for (const name of ['product', 'lifecycle']) await mkdir(path.join(dir, name));
  await writeFile(path.join(dir, 'product/product.json'), JSON.stringify({ steps: [] }));
  await writeFile(path.join(dir, 'product/old.png'), 'old screenshot');
  await writeFile(path.join(dir, 'lifecycle/steps.json'), '[]');
  await writeFile(path.join(dir, 'report.md'), 'previous success');
  const checkout = path.join(dir, 'checkout');
  for (const file of ['frontend/dist/frontend/browser/index.html', 'runner/bin/Debug/net10.0/agent-host.dll',
    'backend/bin/Debug/net10.0/OrchestratorApi.dll', 'scripts/product-start-fixture-check.sh']) {
    const target = path.join(checkout, file);
    await mkdir(path.dirname(target), { recursive: true });
    await writeFile(target, file.endsWith('.sh') ? 'exit 13\n' : '');
  }
  const scripts = 'scenarios/application-lifecycle';
  await mkdir(path.join(checkout, scripts), { recursive: true });
  for (const file of ['run.sh', 'report.mjs']) {
    await symlink(path.resolve(scripts, file), path.join(checkout, scripts, file));
  }
  const runScenario = name => spawnSync('bash', [scripts + '/run.sh', name], {
    cwd: checkout, env: { ...process.env, JOB_RESULTS_DIR: dir }, encoding: 'utf8',
  });
  const run = runScenario('product');
  assert.equal(run.status, 13, run.stderr);
  await assert.rejects(access(path.join(dir, 'product/product.json')), { code: 'ENOENT' });
  await assert.rejects(access(path.join(dir, 'product/old.png')), { code: 'ENOENT' });
  const report = await readFile(path.join(dir, 'report.md'), 'utf8');
  assert.doesNotMatch(report, /previous success/);
  assert.match(report, /lifecycle\/steps.json/);
  assert.match(report, /exited with code 13/);
  assert.doesNotMatch(report, /product\/product.json/);

  // A partial lifecycle attempt keeps the rows it actually reached.
  const row = { title: 'Signed in this time', status: 'passed', evidence: ['New session'], screenshot: 'sign-in.png',
    startedAtUtc: '2026-11-28T00:00:00Z', endedAtUtc: '2026-11-28T00:00:01Z' };
  await writeFile(path.join(dir, 'lifecycle/old-step.png'), 'previous step');
  await writeFile(path.join(checkout, 'scripts/product-start-fixture-check.sh'),
    `echo '${JSON.stringify([row])}' > "$JOB_RESULTS_DIR/lifecycle/steps.json"\ntouch "$JOB_RESULTS_DIR/lifecycle/sign-in.png"\nexit 3\n`);
  const partial = runScenario('lifecycle');
  assert.equal(partial.status, 3, partial.stderr);
  const latest = await readFile(path.join(dir, 'report.md'), 'utf8');
  assert.match(latest, /exited with code 3/);
  assert.match(latest, /Signed in this time/);
  assert.match(latest, /exited with code 13/);
  await assert.rejects(access(path.join(dir, 'lifecycle/old-step.png')), { code: 'ENOENT' });

  // An interrupted sibling write cannot prevent publishing the new attempt.
  await writeFile(path.join(dir, 'lifecycle/steps.json'), '[');
  await writeFile(path.join(checkout, 'scripts/product-start-fixture-check.sh'), 'exit 17\n');
  assert.equal(runScenario('product').status, 17);
  const recovered = await readFile(path.join(dir, 'report.md'), 'utf8');
  assert.match(recovered, /incomplete or invalid JSON/);
  assert.match(recovered, /exited with code 17/);
  assert.doesNotMatch(recovered, /Signed in this time|exited with code 13/);
});
