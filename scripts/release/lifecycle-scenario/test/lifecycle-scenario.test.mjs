import assert from 'node:assert/strict';
import { chmod, mkdtemp, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';

import { UsageError, parseConfig, readSignIn } from '../config.mjs';
import { playScenario } from '../play.mjs';
import { buildReport, renderHtml, writeReport } from '../report.mjs';
import { StepSkipped, overallStatus, quotaDelta, runSteps, waitFor } from '../runner.mjs';
import { totp } from '../session.mjs';
import { environmentDeclarations } from '../steps.mjs';
import { fakeBrowserFactory, fakeClock, startFakeStudio } from './fake-studio.mjs';

const BASE32 = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';

async function scratch(t) {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'lifecycle-scenario-'));
  t.after(() => rm(directory, { recursive: true, force: true }));
  return directory;
}

async function signInFile(directory, fields) {
  const file = path.join(directory, 'sign-in.json');
  await writeFile(file, JSON.stringify(fields));
  await chmod(file, 0o600);
  return file;
}

function argv(studio, file, reportDir, extra = []) {
  return ['--studio-url', studio.url, '--user-file', file, '--report-dir', reportDir,
    '--repository-owner', 'agent-studio-scenarios', '--stage-host', 'v1',
    '--stage-address', 'http://v1.test:8081', '--production-address', 'http://v1.test:8082',
    '--poll-seconds', '5', ...extra];
}

async function play(t, studioOptions = {}, { extra = [], browser = {} } = {}) {
  const clock = fakeClock();
  const studio = await startFakeStudio({ clock, ...studioOptions });
  t.after(() => studio.close());
  const directory = await scratch(t);
  const file = await signInFile(directory, { login: studio.login, password: studio.password,
    ...(studioOptions.totpSecret ? { totpSecret: studioOptions.totpSecret } : {}) });
  const reportDir = path.join(directory, 'report');
  const config = parseConfig(argv(studio, file, reportDir, extra), {});
  const browserFactory = fakeBrowserFactory(studio, browser);
  const report = await playScenario(config, { clock, browserFactory });
  return { report, studio, reportDir, config, clock, browserFactory, file, directory };
}

const step = (report, id) => report.steps.find(row => row.id === id);

test('a full playthrough passes every step and writes the report with evidence', async t => {
  const { report, studio, reportDir } = await play(t);

  assert.equal(report.status, 'passed', JSON.stringify(report.steps.filter(row => row.status !== 'passed'), null, 2));
  assert.deepEqual(report.steps.map(row => row.id), [
    'sign-in-api', 'workspace', 'browser-sign-in', 'create-project', 'declare-environments', 'wish', 'coding',
    'stage', 'production', 'production-failure', 'fix', 'quota-warning',
    'stop-environments', 'remove-project', 'remove-repository', 'remove-workspace']);
  assert.deepEqual(report.addresses, { stage: 'http://v1.test:8081', production: 'http://v1.test:8082' });

  // Production runs the digest stage ran, and the seeded fault rolled back to it.
  const stage = step(report, 'stage').evidence;
  const production = step(report, 'production').evidence;
  assert.match(stage.artifact, /@sha256:[0-9a-f]{64}$/);
  assert.equal(production.approved, stage.artifact);
  const failure = step(report, 'production-failure').evidence;
  assert.equal(failure.rolledBackTo, stage.artifact);
  assert.notEqual(failure.badArtifact, stage.artifact);
  assert.match(failure.failureCard, /^CC-\d+: Investigate Production deployment/);
  assert.notEqual(step(report, 'fix').evidence.productionArtifact, stage.artifact);

  // Quota use per step and in total; the lowered quota blocks new work.
  assert.ok(step(report, 'coding').usage.modelTokens > 0);
  assert.ok(report.totals.modelTokens >= step(report, 'coding').usage.modelTokens);
  const warning = step(report, 'quota-warning').evidence;
  assert.equal(warning.status, 'blocked');
  assert.equal(warning.admission.allowed, false);
  assert.equal(warning.banner.visible, true);

  // Teardown removes project and workspace; the repository is a visible gap.
  assert.equal(report.teardownStatus, 'skipped');
  assert.equal(step(report, 'remove-repository').status, 'skipped');
  assert.match(step(report, 'remove-repository').reason, /no route that deletes a provisioned repository/);
  assert.equal(studio.projects.length, 0);
  assert.ok(!studio.workspaces.some(ws => ws.displayName === 'Department demo'));

  // Timestamps, durations and screenshots per step.
  for (const row of report.steps) {
    assert.ok(!Number.isNaN(Date.parse(row.startedAtUtc)) && !Number.isNaN(Date.parse(row.endedAtUtc)), row.id);
    assert.equal(typeof row.durationSeconds, 'number');
  }
  const shots = report.steps.flatMap(row => row.screenshots.map(shot => shot.file));
  assert.ok(shots.length >= 9, shots.join(', '));
  const files = await readdir(path.join(reportDir, 'screenshots'));
  for (const shot of shots) assert.ok(files.includes(path.basename(shot)), shot);
  const html = await readFile(path.join(reportDir, 'lifecycle.html'), 'utf8');
  assert.match(html, /Result: <strong>passed<\/strong>/);
  assert.match(html, /http:\/\/v1\.test:8081/);
});

test('the report and the requests never carry the password', async t => {
  const { studio, reportDir } = await play(t);
  for (const name of ['lifecycle.json', 'lifecycle.html'])
    assert.ok(!(await readFile(path.join(reportDir, name), 'utf8')).includes(studio.password), name);
  const leaked = studio.requests.filter(request => request.path !== '/api/auth/login' && request.body.includes(studio.password));
  assert.deepEqual(leaked, []);
});

test('every write carries the session CSRF token and a same-origin header', async t => {
  const { studio } = await play(t);
  const writes = studio.requests.filter(request => request.method !== 'GET' && request.path !== '/api/auth/login');
  assert.ok(writes.length > 10);
  for (const request of writes) {
    assert.ok(request.headers['x-csrf-token'], `${request.method} ${request.path}`);
    assert.equal(request.headers['sec-fetch-site'], 'same-origin');
  }
});

test('the stage and production declarations give production the stricter health contract', () => {
  const config = parseConfig(['--studio-url', 'https://studio.v1.test', '--user-file', '/dev/null',
    '--repository-owner', 'org', '--stage-host', 'v1', '--stage-address', 'http://v1.test:8081/',
    '--production-address', 'http://v1.test:8082'], {});
  const { stage, production } = environmentDeclarations(config);
  assert.equal(stage.healthUrl, 'http://v1.test:8081/');
  assert.equal(production.healthUrl, 'http://v1.test:8082/health.txt');
  assert.equal(production.approval, 'person');
  assert.equal(stage.destination, '/srv/agent-studio/sites/click-counter-stage');
  assert.equal(production.targetHostId, 'v1');
  for (const declaration of [stage, production])
    for (const field of ['id', 'name', 'kind', 'targetHostId', 'address', 'template', 'destination', 'healthUrl'])
      assert.ok(declaration[field], `${declaration.id}.${field}`);
});

test('a rerun after a run without teardown removes the leftover project and passes again', async t => {
  const clock = fakeClock();
  const studio = await startFakeStudio({ clock });
  t.after(() => studio.close());
  const directory = await scratch(t);
  const file = await signInFile(directory, { login: studio.login, password: studio.password });
  const first = await playScenario(parseConfig(argv(studio, file, path.join(directory, 'one'), ['--teardown', 'never']), {}),
    { clock, browserFactory: fakeBrowserFactory(studio) });
  assert.equal(first.status, 'passed');
  assert.equal(first.teardownStatus, 'not-run');
  assert.equal(studio.projects.length, 1);

  const second = await playScenario(parseConfig(argv(studio, file, path.join(directory, 'two')), {}),
    { clock, browserFactory: fakeBrowserFactory(studio) });
  assert.equal(second.status, 'passed', JSON.stringify(second.steps.filter(row => row.status !== 'passed'), null, 2));
  assert.equal(step(second, 'workspace').evidence.reusedWorkspace, true);
  assert.equal(step(second, 'workspace').evidence.removedLeftoverProjects.length, 1);
  assert.notEqual(step(second, 'create-project').evidence.repository, step(first, 'create-project').evidence.repository);
  assert.equal(studio.projects.length, 0);
});

test('teardown only removes what an earlier run left behind', async t => {
  const clock = fakeClock();
  const studio = await startFakeStudio({ clock });
  t.after(() => studio.close());
  const directory = await scratch(t);
  const file = await signInFile(directory, { login: studio.login, password: studio.password });
  await playScenario(parseConfig(argv(studio, file, path.join(directory, 'one'), ['--teardown', 'never']), {}),
    { clock, browserFactory: fakeBrowserFactory(studio) });
  const cleanup = await playScenario(parseConfig(['--studio-url', studio.url, '--user-file', file,
    '--report-dir', path.join(directory, 'two'), '--teardown', 'only'], {}), { clock, browserFactory: fakeBrowserFactory(studio) });
  assert.deepEqual(cleanup.steps.map(row => row.id), ['stop-environments', 'remove-project', 'remove-repository', 'remove-workspace']);
  assert.deepEqual(step(cleanup, 'stop-environments').evidence.stopped, ['stage', 'production']);
  assert.equal(studio.projects.length, 0);
  assert.ok(!studio.workspaces.some(ws => ws.displayName === 'Department demo'));
});

test('a red review stops the journey with the reasons, skips what depends on it, and still tears down', async t => {
  const { report, studio } = await play(t, { redReviewTitles: ['History of the last ten values'] });
  assert.equal(report.status, 'failed');
  const coding = step(report, 'coding');
  assert.equal(coding.status, 'failed');
  assert.match(coding.reason, /stays in Human Review/);
  assert.ok(coding.evidence.reasons.length > 0);
  for (const id of ['stage', 'production', 'production-failure', 'fix', 'quota-warning']) {
    assert.equal(step(report, id).status, 'skipped', id);
    assert.match(step(report, id).reason, /did not pass/);
  }
  assert.equal(step(report, 'remove-project').status, 'passed');
  assert.equal(studio.projects.length, 0);
});

test('a failed stage deployment fails step 5 with the release in the evidence', async t => {
  const { report } = await play(t, { stageFailsFor: ['History of the last ten values'] });
  const stage = step(report, 'stage');
  assert.equal(stage.status, 'failed');
  assert.match(stage.reason, /Stage failed for release/);
  assert.ok(stage.evidence.release);
});

test('the wish confirms once when the orchestrator proposes before it creates cards', async t => {
  const { report } = await play(t, { chatNeedsConfirmation: true });
  const wish = step(report, 'wish');
  assert.equal(wish.status, 'passed', wish.reason);
  assert.match(wish.evidence.reply, /Shall I create/);
  assert.equal(wish.evidence.cards.length, 2);
  assert.ok(wish.evidence.confirmation);
});

test('an Owner with an authenticator signs in without replaying a code', async t => {
  const secret = Array.from({ length: 32 }, (_, index) => BASE32[(index * 11 + 5) % 32]).join('');
  const { report } = await play(t, { totpSecret: secret });
  assert.equal(step(report, 'sign-in-api').status, 'passed', step(report, 'sign-in-api').reason);
  assert.equal(report.status, 'passed', JSON.stringify(report.steps.filter(row => row.status !== 'passed'), null, 2));
});

test('a user who is not the Owner stops at sign-in with the reason', async t => {
  const { report } = await play(t, { role: 'operator' });
  assert.equal(step(report, 'sign-in-api').status, 'failed');
  assert.match(step(report, 'sign-in-api').reason, /must be the Owner/);
  assert.equal(step(report, 'create-project').status, 'skipped');
});

test('a wrong password fails the start step without writing the password', async t => {
  const clock = fakeClock();
  const studio = await startFakeStudio({ clock });
  t.after(() => studio.close());
  const directory = await scratch(t);
  const file = await signInFile(directory, { login: studio.login, password: 'not-the-password' });
  const reportDir = path.join(directory, 'report');
  const report = await playScenario(parseConfig(argv(studio, file, reportDir), {}), { clock, browserFactory: fakeBrowserFactory(studio) });
  assert.equal(step(report, 'sign-in-api').status, 'failed');
  assert.match(step(report, 'sign-in-api').reason, /401/);
  assert.ok(!(await readFile(path.join(reportDir, 'lifecycle.json'), 'utf8')).includes('not-the-password'));
});

test('configuration refuses passwords and tokens outside the sign-in file', async t => {
  const required = ['--studio-url', 'https://s.test', '--user-file', 'f', '--repository-owner', 'o',
    '--stage-host', 'v1', '--stage-address', 'http://a.test', '--production-address', 'http://b.test'];
  assert.throws(() => parseConfig([...required, '--password', 'x'], {}), UsageError);
  const tokenShaped = ['api', 'a'.repeat(32), 'b'.repeat(43)].join('.');
  assert.throws(() => parseConfig(required, { SOME_VALUE: tokenShaped }), /token-shaped/);
  assert.throws(() => parseConfig(required.slice(2), {}), /--studio-url is required/);
  assert.throws(() => parseConfig(required.filter((_, i) => i < 8 || i > 9), {}), /--stage-address is required/);
  assert.throws(() => parseConfig([...required, '--teardown', 'sometimes'], {}), /--teardown must be one of/);
  assert.throws(() => parseConfig([...required, '--quota-tokens', '1.5'], {}), /whole number/);
  assert.equal(parseConfig([...required, '--production-host', 'v2'], {}).production.host, 'v2');
  assert.equal(parseConfig(required, {}).production.host, 'v1');

  const directory = await scratch(t);
  const open = path.join(directory, 'open.json');
  await writeFile(open, JSON.stringify({ login: 'a', password: 'b' }));
  await chmod(open, 0o644);
  if (process.platform !== 'win32') assert.throws(() => readSignIn(open), /accessible to group or others/);
  const bad = await signInFile(directory, { login: 'a', password: 'b', totpSecret: 'not base32!' });
  assert.throws(() => readSignIn(bad), /base32/);
  const good = await signInFile(directory, { login: 'a', password: 'b' });
  assert.deepEqual(readSignIn(good), { login: 'a', password: 'b' });
});

test('totp follows the RFC 6238 SHA-1 vectors', () => {
  const secret = 'GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ';
  assert.equal(totp(secret, 59_000), '287082');
  assert.equal(totp(secret, 1_111_111_109_000), '081804');
  assert.equal(totp(secret, 2_000_000_000_000), '279037');
});

test('the runner skips a step whose prerequisite failed and records a gap as skipped', async () => {
  const clock = fakeClock();
  const rows = await runSteps([
    { id: 'a', title: 'A', run: async () => { throw new Error('boom'); } },
    { id: 'b', title: 'B', needs: ['a'], run: async () => {} },
    { id: 'c', title: 'C', needs: ['a'], always: true, phase: 'teardown', run: async () => {} },
    { id: 'd', title: 'D', phase: 'teardown', run: async () => { throw new StepSkipped('no route', { gap: 'x' }); } },
  ], { context: {}, clock });
  assert.deepEqual(rows.map(row => row.status), ['failed', 'skipped', 'passed', 'skipped']);
  assert.match(rows[0].reason, /boom/);
  assert.match(rows[1].reason, /Needs step "a"/);
  assert.deepEqual(rows[3].evidence, { gap: 'x' });
  assert.equal(overallStatus(rows.filter(row => row.phase !== 'teardown')), 'failed');
});

test('waitFor times out on the injected clock with the awaited condition in the reason', async () => {
  const clock = fakeClock();
  await assert.rejects(waitFor('the moon', async () => null, { clock, timeoutMs: 60_000, intervalMs: 10_000 }),
    /Timed out after 60 s waiting for the moon/);
});

test('quota deltas are per step and never negative', () => {
  assert.deepEqual(quotaDelta({ usage: { modelTokens: 10, computeCoreHours: 0.5 } }, { usage: { modelTokens: 25, computeCoreHours: 0.75 } }),
    { modelTokens: 15, computeCoreHours: 0.25 });
  assert.deepEqual(quotaDelta({ usage: { modelTokens: 10, computeCoreHours: 1 } }, { usage: { modelTokens: 5, computeCoreHours: 0 } }),
    { modelTokens: 0, computeCoreHours: 0 });
  assert.equal(quotaDelta(null, { usage: {} }), null);
});

test('the report scrubs a long secret but leaves short values and step ids intact', async t => {
  const directory = await scratch(t);
  const secret = 'correct-horse-battery';
  const report = buildReport({ scenario: { id: 'lifecycle', description: 'd' },
    run: { runId: 'r', studioOrigin: 'o', platform: 'p', workspace: 'w', project: 'p', startedAtUtc: '', endedAtUtc: '', durationSeconds: 0 },
    steps: [{ id: 'browser-sign-in', title: 'browser-sign-in', phase: 'scenario', status: 'failed', startedAtUtc: '', endedAtUtc: '',
      durationSeconds: 0, reason: `refused ${secret}`, evidence: {}, screenshots: [], usage: null }], addresses: {} });
  await writeReport(directory, report, [secret, 'b']);
  const written = await readFile(path.join(directory, 'lifecycle.json'), 'utf8');
  assert.ok(!written.includes(secret));
  assert.ok(written.includes('"id": "browser-sign-in"'));
  assert.ok(written.includes('refused [redacted]'));
});

test('the HTML report escapes evidence and keeps teardown out of the verdict', () => {
  const row = (id, phase, status, evidence = {}) => ({ id, title: id, phase, status, startedAtUtc: '2026-11-09T08:00:00.000Z',
    endedAtUtc: '2026-11-09T08:00:01.000Z', durationSeconds: 1, evidence, screenshots: [], usage: null });
  const report = buildReport({
    scenario: { id: 'lifecycle', description: 'Lifecycle' },
    run: { runId: 'r', studioOrigin: 'https://s.test', platform: 'v1', workspace: 'W', project: 'P',
      startedAtUtc: '2026-11-09T08:00:00.000Z', endedAtUtc: '2026-11-09T08:00:02.000Z', durationSeconds: 2 },
    steps: [row('a', 'scenario', 'passed', { reply: '<script>x</script>' }), row('t', 'teardown', 'skipped')],
    addresses: {},
  });
  assert.equal(report.status, 'passed');
  assert.equal(report.teardownStatus, 'skipped');
  const html = renderHtml(report);
  assert.ok(html.includes('&lt;script&gt;x&lt;/script&gt;'));
  assert.ok(!html.includes('<script>x'));
  assert.match(html, /Stage: not reached/);
});
