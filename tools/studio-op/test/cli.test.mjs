// Run: node --test tools/studio-op/test/cli.test.mjs
//
// OP-8 (Dossier operator-agent-level): studio-op runs every working-set
// operation, never prints the token, holds one watcher at a time, and refuses
// to accept a card without green evidence. A fake Studio echoes the bearer in
// every answer, so an unredacted print would show up here.
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { card, fakeToken, remoteAttempt, reviewProjection, startFakeStudio } from './fake-studio.mjs';

const bin = path.join(path.dirname(fileURLToPath(import.meta.url)), '..', 'studio-op.mjs');
const posix = process.platform !== 'win32';
const token = fakeToken();
const outputs = [];
let studio;

function makeHome(value = token) {
  const home = fs.mkdtempSync(path.join(os.tmpdir(), 'studio-op-home-'));
  const folder = path.join(home, '.config', 'agent-studio');
  fs.mkdirSync(folder, { recursive: true, mode: 0o700 });
  if (value !== null) {
    const file = path.join(folder, 'operator-token');
    fs.writeFileSync(file, value + '\n', { mode: 0o600 });
    fs.chmodSync(file, 0o600);
  }
  return home;
}

function environment(home, extra = {}) {
  const env = { PATH: process.env.PATH, HOME: home, USERPROFILE: home, STUDIO_OP_URL: studio.url, ...extra };
  if (process.env.SystemRoot) env.SystemRoot = process.env.SystemRoot;
  return env;
}

/** Starts studio-op; resolves with its exit code and output once it ends. */
function start(args, { home, env = {} } = {}) {
  const child = spawn(process.execPath, [bin, ...args], { env: environment(home, env), windowsHide: true });
  let stdout = '';
  let stderr = '';
  child.stdout.on('data', chunk => { stdout += chunk; });
  child.stderr.on('data', chunk => { stderr += chunk; });
  const done = new Promise(resolve => child.on('close', code => {
    outputs.push({ args, stdout, stderr });
    resolve({ code, stdout, stderr, json: parseJson(stdout) });
  }));
  return { child, done, stderr: () => stderr };
}

function run(args, options) {
  return start(args, options).done;
}

function parseJson(text) {
  try { return JSON.parse(text); } catch { return null; }
}

async function until(predicate, label, timeoutMs = 10_000) {
  const deadline = Date.now() + timeoutMs;
  while (!predicate()) {
    if (Date.now() > deadline) throw new Error(`Timed out waiting for ${label}`);
    await new Promise(resolve => setTimeout(resolve, 25));
  }
}

const promptFile = path.join(fs.mkdtempSync(path.join(os.tmpdir(), 'studio-op-prompt-')), 'prompt.md');
fs.writeFileSync(promptFile, 'Address the review finding on the parser.\n');

before(async () => {
  studio = await startFakeStudio({
    cards: {
      'DEMO-1': { card: card('DEMO-1'), projection: reviewProjection(), warming: 2 },
      'DEMO-2': { card: card('DEMO-2'), projection: reviewProjection({
        attempts: [remoteAttempt({ outcome: 'ProductFailure' })],
        blockingAspects: [{ aspect: 'code-quality', reason: 'Blocked.' }] }) },
      'DEMO-3': { card: card('DEMO-3'), projection: reviewProjection({ attempts: [] }) },
      'DEMO-4': { card: card('DEMO-4', { state: '2-ready' }) },
      'DEMO-5': { card: card('DEMO-5'), projection: reviewProjection(), contentGap: true },
    },
  });
});

after(async () => {
  await studio.close();
  // No output of any command, success or refusal, carries the token.
  assert.ok(outputs.length > 15, 'the suite ran the commands it greps');
  for (const { args, stdout, stderr } of outputs) {
    assert.ok(!stdout.includes(token), `stdout of studio-op ${args.join(' ')} contains the token`);
    assert.ok(!stderr.includes(token), `stderr of studio-op ${args.join(' ')} contains the token`);
  }
});

test('every working-set command answers with JSON and exit 0', async () => {
  const home = makeHome();
  const commands = [
    ['auth', 'status'],
    ['triage'],
    ['eject', 'DEMO-4', '--project', 'Demo App'],
    ['continue', 'DEMO-4', '--prompt-file', promptFile, '--reason', 'Review finding'],
    ['move', 'DEMO-4', '5-human-review'],
    ['create', '--ready', '--project', 'Demo App', '--title', 'Follow-up from triage', '--prompt-file', promptFile],
    ['title', 'DEMO-4', 'Repaired title'],
    ['capacity'],
    ['backups'],
    ['security'],
    ['routing'],
    ['routing', 'set-step', '--step', 'remote-review', '--cli', 'codex', '--model', 'demo-model', '--all'],
    ['preflight', 'restart'],
    ['preflight', 'deploy'],
    ['deploy', 'plan', '--target-version', '1.2.3'],
    ['log', 'append', '--project', 'Demo App', '--kind', 'did', '--text', 'Moved DEMO-4.', '--card', 'DEMO-4'],
    ['log', '--project', 'Demo App'],
  ];
  for (const command of commands) {
    const result = await run([...command, '--json'], { home });
    assert.equal(result.code, 0, `studio-op ${command.join(' ')}: ${result.stdout}${result.stderr}`);
    assert.ok(result.json?.ok, `studio-op ${command.join(' ')} prints a JSON result`);
  }
  const cardWrites = studio.requests.filter(request =>
    /^\/api\/tasks\/DEMO-4\/(move|continue|title)$/.test(request.path));
  assert.equal(cardWrites.length, 3);
  for (const request of cardWrites) assert.match(request.ifMatch ?? '', /^"r\d+"$/, `${request.path} carries the revision`);
  assert.ok(studio.requests.every(request => request.authorization === `Bearer ${token}`));
  const continued = studio.requests.find(request => request.path === '/api/tasks/DEMO-4/continue');
  assert.equal(continued.body.prompt, 'Address the review finding on the parser.\n');
  const create = studio.requests.find(request => request.method === 'POST' && request.path === '/api/tasks/');
  assert.equal(create.body.watchPath, '/demo/app');
  assert.equal(create.body.targetState, '2-ready');
  assert.equal(create.body.project, undefined, 'a display name is not a project handle of the create route');
  const step = studio.requests.filter(request => request.method === 'PUT' && request.path.endsWith('/pipeline-step'));
  assert.deepEqual(step.map(request => request.path), ['/api/projects/Demo%20App/pipeline-step']);
});

test('the human-readable output is redacted as well', async () => {
  const home = makeHome();
  for (const command of [['auth', 'status'], ['triage'], ['capacity'], ['backups'], ['log', '--project', 'Demo App']]) {
    const result = await run(command, { home });
    assert.equal(result.code, 0, result.stderr);
    assert.ok(result.stdout.length > 0);
  }
});

test('backups prints the backup state and its warnings from the operator route (AGT-3574)', async () => {
  const home = makeHome();
  const result = await run(['backups'], { home });
  assert.equal(result.code, 0, result.stderr);
  assert.match(result.stdout, /backups failing/);
  assert.match(result.stdout, /restore verification verified/);
  assert.match(result.stdout, /off-host sftp: current/);
  assert.match(result.stdout, /warning backup-stale: The newest good backup is 13 hours old\./);
  assert.ok(studio.requests.some(request => request.method === 'GET' && request.path === '/api/operator/backups'));
});

test('security lists ranked entries and narrows to what needs action soon', async () => {
  const home = makeHome();
  const all = await run(['security'], { home });
  assert.equal(all.code, 0, all.stderr);
  assert.match(all.stdout, /2 entries \(1 critical, 0 high, 1 medium, 0 low, 0 info\); 1 need action soon\./);
  assert.match(all.stdout, /#1 critical 95 second-factor: Owner has no second factor \[action soon\]/);
  assert.match(all.stdout, /#2 medium 50 credentials: API token ci never expires/);
  const soon = await run(['security', '--soon'], { home });
  assert.equal(soon.code, 0, soon.stderr);
  assert.doesNotMatch(soon.stdout, /never expires/);
  const allJson = await run(['security', '--json'], { home });
  assert.equal(allJson.code, 0, allJson.stderr);
  assert.equal(allJson.json.soonOnly, false);
  assert.deepEqual(allJson.json.findings.map(finding => finding.rank), [1, 2]);
  const soonJson = await run(['security', '--soon', '--json'], { home });
  assert.equal(soonJson.code, 0, soonJson.stderr);
  assert.equal(soonJson.json.soonOnly, true);
  assert.deepEqual(soonJson.json.findings.map(finding => finding.id), ['second-factor:u1'], '--soon narrows the JSON findings');
  assert.equal(soonJson.json.summary.total, 2, 'the summary keeps the installation-wide counts');
  assert.ok(studio.requests.some(request => request.method === 'GET' && request.path === '/api/security/posture'));
});

test('accept refuses red and missing evidence and accepts a green card', async () => {
  const home = makeHome();
  const red = await run(['accept', 'DEMO-2', '--json'], { home });
  assert.equal(red.code, 7);
  assert.equal(red.json.error, 'accept-refused');
  assert.deepEqual(red.json.reasons.map(reason => reason.code), ['evidence-red']);
  const missing = await run(['accept', 'DEMO-3'], { home });
  assert.equal(missing.code, 7);
  assert.match(missing.stderr, /evidence-missing/);
  assert.ok(!studio.requests.some(request => /DEMO-(2|3)\/move$/.test(request.path)), 'a refused accept moves nothing');

  // Studio's own completion check refuses a card without a current content verdict.
  const gap = await run(['accept', 'DEMO-5', '--json'], { home });
  assert.equal(gap.code, 7);
  assert.deepEqual(gap.json.reasons.map(reason => reason.code), ['evidence-missing']);
  assert.match(gap.json.reasons[0].detail, /content verdict/);

  // DEMO-1 first answers twice with 202 task-index-warming; accept waits for the card.
  const green = await run(['accept', 'DEMO-1', '--json'], { home });
  assert.equal(green.code, 0, green.stdout + green.stderr);
  const move = studio.requests.find(request => request.path === '/api/tasks/DEMO-1/move');
  assert.equal(move.body.targetState, '6-completed');
  assert.ok(move.ifMatch);
  const logged = studio.requests.find(request => request.method === 'POST' && request.path === '/api/operator/log'
    && request.body.cardKeys?.includes('DEMO-1'));
  assert.equal(logged.body.kind, 'did');
  assert.match(logged.body.text, /operator verdict/i);
});

test('move refuses the accepted lane and points to accept', async () => {
  const result = await run(['move', 'DEMO-1', 'done', '--json'], { home: makeHome() });
  assert.equal(result.code, 2);
  assert.equal(result.json.error, 'use-accept');
});

test('preflight names its blockers and exits with the preflight code', async () => {
  const blocked = await startFakeStudio({ preflightBlockers: [
    { code: 'pending-push', project: 'Demo App', subject: 'DEMO-7', detail: 'Push pending for 9 minutes.' }] });
  try {
    const home = makeHome();
    const result = await run(['preflight', 'restart'], { home, env: { STUDIO_OP_URL: blocked.url } });
    assert.equal(result.code, 8);
    assert.match(result.stdout + result.stderr, /pending-push.*DEMO-7/s);
  } finally { await blocked.close(); }
});

test('two concurrent watchers: the second exits with the watcher-active code', { timeout: 30_000 }, async () => {
  const homeA = makeHome();
  const first = start(['watch', '--session', 'first', '--json'], { home: homeA });
  await until(() => /watching/i.test(first.stderr()), 'the first watcher to connect');
  assert.equal(studio.lease?.session, 'first');

  // Another machine: the server lease refuses it.
  const remote = await run(['watch', '--session', 'second', '--json'], { home: makeHome() });
  assert.equal(remote.code, 9, remote.stdout + remote.stderr);
  assert.equal(remote.json.error, 'watcher-active');
  assert.equal(remote.json.session, 'first');
  // The same machine: the local lock file refuses it before any request.
  const requestsBefore = studio.requests.length;
  const local = await run(['watch', '--json'], { home: homeA });
  assert.equal(local.code, 9);
  assert.equal(local.json.error, 'watcher-active');
  assert.equal(local.json.scope, 'local-lock');
  assert.equal(studio.requests.length, requestsBefore);

  studio.publish('watcher.heartbeat', { project: null, cardKey: null });
  const event = studio.publish('human-review.ready', { cardKey: 'DEMO-1' });
  const result = await first.done;
  assert.equal(result.code, 21, result.stdout + result.stderr);
  assert.equal(result.json.event.type, 'human-review.ready');
  assert.equal(result.json.event.cardKey, 'DEMO-1');
  assert.equal(result.json.cursor, event.cursor);
  assert.equal(studio.lease, null, 'the watcher releases its lease on exit');
  assert.ok(!fs.existsSync(path.join(homeA, '.config', 'agent-studio', 'studio-op-watch.lock')));

  // The next watch resumes after the saved cursor and wakes on the next event.
  const next = start(['watch', '--json'], { home: homeA });
  await until(() => /watching/i.test(next.stderr()), 'the resumed watcher');
  studio.publish('batch.red', { cardKey: null });
  const resumed = await next.done;
  assert.equal(resumed.code, 24);
  assert.equal(resumed.json.event.type, 'batch.red');
});

test('watch with a stale local lock of a dead process takes the lock', { timeout: 30_000 }, async () => {
  const home = makeHome();
  const lock = path.join(home, '.config', 'agent-studio', 'studio-op-watch.lock');
  fs.writeFileSync(lock, JSON.stringify({ pid: 2 ** 22 + 17, startedAt: '2026-11-05T09:00:00Z' }), { mode: 0o600 });
  const watcher = start(['watch', '--timeout', '1', '--json'], { home });
  const result = await watcher.done;
  assert.equal(result.code, 0, result.stdout + result.stderr);
  assert.equal(result.json.event, null);
  assert.equal(result.json.reason, 'timeout');
});

test('refuses every token carrier other than the file, before any request', async () => {
  const retired = ['AGENT', 'STUDIO', 'API', 'TOKEN'].join('_');
  const before = studio.requests.length;
  const environmentToken = await run(['triage', '--json'], { home: makeHome(null), env: { [retired]: token } });
  assert.equal(environmentToken.code, 3);
  assert.equal(environmentToken.json.error, 'token-environment-refused');
  const option = await run(['triage', '--token', 'x', '--json'], { home: makeHome() });
  assert.equal(option.code, 3);
  assert.equal(option.json.error, 'token-argument-refused');
  const argument = await run(['log', 'append', '--project', 'Demo App', '--text', token, '--json'], { home: makeHome() });
  assert.equal(argument.code, 3);
  assert.equal(argument.json.error, 'token-argument-refused');
  assert.equal(studio.requests.length, before);
});

test('refuses a token file readable by others', { skip: !posix }, async () => {
  const home = makeHome();
  fs.chmodSync(path.join(home, '.config', 'agent-studio', 'operator-token'), 0o644);
  const result = await run(['auth', 'status', '--json'], { home });
  assert.equal(result.code, 3);
  assert.equal(result.json.error, 'token-file-permissions');
});

test('auth status reports a missing token file with ok false and exit 3', async () => {
  const result = await run(['auth', 'status', '--json'], { home: makeHome(null), env: { STUDIO_OP_URL: 'https://studio.example.test' } });
  assert.equal(result.code, 3);
  assert.equal(result.json.ok, false);
  assert.equal(result.json.credential.exists, false);
  assert.equal(result.json.server.error, 'token-file-missing');
});

test('a networked URL without a token file is refused', async () => {
  const result = await run(['triage', '--json'], { home: makeHome(null), env: { STUDIO_OP_URL: 'https://studio.example.test' } });
  assert.equal(result.code, 3);
  assert.equal(result.json.error, 'token-file-missing');
});

test('a prompt file holding a token-shaped value is refused before it is sent', async () => {
  const leaking = path.join(path.dirname(promptFile), 'leaking.md');
  fs.writeFileSync(leaking, `Use ${fakeToken('sat')} to log in.\n`);
  const result = await run(['continue', 'DEMO-4', '--prompt-file', leaking, '--json'], { home: makeHome() });
  assert.equal(result.code, 2);
  assert.equal(result.json.error, 'token-in-content');
});

test('usage errors and help', async () => {
  const home = makeHome();
  const unknown = await run(['frobnicate', '--json'], { home });
  assert.equal(unknown.code, 2);
  assert.equal(unknown.json.error, 'unknown-command');
  const help = await run(['--help'], { home });
  assert.equal(help.code, 0);
  assert.match(help.stdout, /studio-op watch/);
});
