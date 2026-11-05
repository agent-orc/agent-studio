import { spawn, type ChildProcess } from 'node:child_process';
import { createHash, createHmac } from 'node:crypto';
import { chmodSync, existsSync, mkdirSync, mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { basename, join, resolve } from 'node:path';
import { test, expect, request as playwrightRequest, type APIRequestContext } from '@playwright/test';

/**
 * OP-8 (Dossier operator-agent-level): every operation of the operator's
 * working set runs through studio-op against the prebuilt networked stack,
 * with an operator API token read from a file. No command prints the token,
 * a second watcher exits with the watcher-active code, and accept refuses red
 * or missing evidence.
 */
const repository = resolve(__dirname, '..', '..', '..');
const bin = join(repository, 'tools', 'studio-op', 'studio-op.mjs');
const resultDir = join(process.env.JOB_RESULTS_DIR ?? 'test-results', 'studio-op-e2e');
const project = 'Agent Studio Worktree';

interface Run { args: string[]; code: number | null; stdout: string; stderr: string; json: any }
const runs: Run[] = [];

function base32(secret: string): Buffer {
  const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
  let bits = '';
  for (const char of secret.replace(/=+$/, '').toUpperCase()) bits += alphabet.indexOf(char).toString(2).padStart(5, '0');
  const bytes: number[] = [];
  for (let index = 0; index + 8 <= bits.length; index += 8) bytes.push(parseInt(bits.slice(index, index + 8), 2));
  return Buffer.from(bytes);
}

/** RFC 6238 code, the same algorithm the authenticator app uses. */
function totp(secret: string, at = Date.now()): string {
  const counter = Buffer.alloc(8);
  counter.writeBigUInt64BE(BigInt(Math.floor(at / 30_000)));
  const digest = createHmac('sha1', base32(secret)).update(counter).digest();
  const offset = digest[digest.length - 1] & 0x0f;
  return String((digest.readUInt32BE(offset) & 0x7fffffff) % 1_000_000).padStart(6, '0');
}

function makeHome(token: string, url: string): string {
  const home = mkdtempSync(join(tmpdir(), 'studio-op-e2e-home-'));
  const folder = join(home, '.config', 'agent-studio');
  mkdirSync(folder, { recursive: true, mode: 0o700 });
  const file = join(folder, 'operator-token');
  writeFileSync(file, `${token}\n`, { mode: 0o600 });
  chmodSync(file, 0o600);
  writeFileSync(join(folder, 'studio-op.json'), JSON.stringify({ url }), { mode: 0o600 });
  return home;
}

function start(args: string[], home: string): { child: ChildProcess; done: Promise<Run>; stderr: () => string } {
  // Only what the CLI needs: no inherited variable can carry a token.
  const env: Record<string, string> = { PATH: process.env.PATH ?? '', HOME: home, USERPROFILE: home };
  if (process.env.SystemRoot) env.SystemRoot = process.env.SystemRoot;
  const child = spawn(process.execPath, [bin, ...args], { env, windowsHide: true });
  let stdout = '';
  let stderr = '';
  child.stdout!.on('data', chunk => { stdout += chunk; });
  child.stderr!.on('data', chunk => { stderr += chunk; });
  const done = new Promise<Run>(settle => child.on('close', code => {
    let json: any = null;
    try { json = JSON.parse(stdout); } catch { json = null; }
    const run = { args, code, stdout, stderr, json };
    runs.push(run);
    settle(run);
  }));
  return { child, done, stderr: () => stderr };
}

const studioOp = (home: string, ...args: string[]) => start([...args, '--json'], home).done;

async function until(predicate: () => boolean | Promise<boolean>, label: string, timeoutMs = 20_000): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  while (!(await predicate())) {
    if (Date.now() > deadline) throw new Error(`Timed out waiting for ${label}`);
    await new Promise(settle => setTimeout(settle, 200));
  }
}

function reviewReport(attemptId: string, outcome: string, rows: string[]): string {
  const sha = 'c'.repeat(40);
  return [
    '---', 'type: remote-review-grade', `attemptId: "${attemptId}"`, `receivedAt: ${new Date().toISOString().replace(/\.\d+Z$/, 'Z')}`,
    `outcome: "${outcome}"`, `expectedResultSha: "${sha}"`, `actualHead: "${sha}"`, '---', '',
    '## Aspect verdicts', '', '| Aspect | Status | Classification | Summary |', '| --- | --- | --- | --- |', ...rows, '',
  ].join('\n');
}

// One fresh stack per run: a retry would meet an Owner that already exists.
test.describe.configure({ mode: 'serial', retries: 0 });

test('studio-op runs the operator working set against the prebuilt stack', async () => {
  test.setTimeout(300_000);
  const baseURL = process.env.PW_BASE_URL!;
  const workspace = process.env.PW_E2E_WORKSPACE!;
  expect(baseURL).toBeTruthy();
  mkdirSync(resultDir, { recursive: true });

  // The Owner: bootstrap, enroll the second factor, create the operator token.
  const anonymous = await playwrightRequest.newContext({ baseURL });
  const password = ['long', 'enough', 'e2e', 'passphrase'].join('-');
  const bootstrap = await anonymous.post('/api/auth/bootstrap', { headers: { 'Sec-Fetch-Site': 'same-origin' },
    data: { username: 'studio-owner', displayName: 'Owner', password, bootstrapCode: `${basename(workspace)}-owner-code` } });
  expect(bootstrap.ok(), `${bootstrap.status()}: ${await bootstrap.text()}`).toBeTruthy();
  const session = bootstrap.headers()['set-cookie']?.match(/__Host-agentstudio-session=([^;,\s]+)/)?.[1];
  const csrf = (await bootstrap.json()).csrfToken as string;
  expect(session && csrf).toBeTruthy();
  // A freshly started stack indexes and probes Git on its first lane moves.
  const owner: APIRequestContext = await playwrightRequest.newContext({ baseURL, timeout: 60_000, extraHTTPHeaders: {
    Cookie: `__Host-agentstudio-session=${session}`, 'X-CSRF-Token': csrf, 'Sec-Fetch-Site': 'same-origin',
    'X-Client-Id': 'local-default' } });
  const factor = await owner.post('/api/auth/totp/start');
  expect(factor.ok(), await factor.text()).toBeTruthy();
  const confirm = await owner.post('/api/auth/totp/confirm', { data: { code: totp((await factor.json()).secret) } });
  expect(confirm.ok(), await confirm.text()).toBeTruthy();
  const created = await owner.post('/api/auth/api-tokens', { data: { name: 'studio-op e2e', role: 'operator',
    projects: ['*'], expiresAt: new Date(Date.now() + 7 * 86_400_000).toISOString() } });
  expect(created.ok(), await created.text()).toBeTruthy();
  const token = (await created.json()).secret as string;
  expect(token).toMatch(/^api\./);

  const home = makeHome(token, baseURL);
  const otherMachine = makeHome(token, baseURL);
  const promptFile = join(home, 'prompt.md');
  writeFileSync(promptFile, 'Add the missing review evidence for the parser change.\n');
  const watchPath = join(workspace, 'projects', 'agent-studio-worktree');

  const ownerCard = async (title: string) => {
    const response = await owner.post('/api/tasks', { data: { id: '', title, watchPath, agent: 'claude',
      cliType: 'claude', targetState: '2-ready', requiresIntegration: false, fixture: false,
      promptMarkdown: `Fixture card for the studio-op end-to-end test: ${title}.` } });
    expect(response.ok(), await response.text()).toBeTruthy();
    const info = await ownerRead({ id: (await response.json()).id });
    return info as { id: string; key: string; folderPath: string; state: string; title: string };
  };
  const ownerMove = async (card: { id: string }, lane: string) => {
    const moved = await owner.post(`/api/tasks/${encodeURIComponent(card.id)}/move`, { params: { watchPath },
      data: { targetState: lane } });
    expect(moved.ok(), await moved.text()).toBeTruthy();
  };
  // The detail route answers 202 task-index-warming until the index has the card.
  const ownerRead = async (card: { id: string }): Promise<any> => {
    for (let attempt = 0; attempt < 60; attempt++) {
      const detail = await owner.get(`/api/tasks/${encodeURIComponent(card.id)}`, { params: { watchPath } });
      // The card itself is `info`, beside its prompt and history.
      if (detail.status() === 200) return (await detail.json()).info;
      expect(detail.status(), await detail.text()).toBe(202);
      await new Promise(settle => setTimeout(settle, 500));
    }
    throw new Error(`Card ${card.id} stayed unreadable`);
  };
  const ownerDecisions = async () =>
    (await (await owner.get('/api/operator/decisions', { params: { limit: '1000' } })).json()).events as any[];

  await test.step('auth status names the credential file and the identity check', async () => {
    const status = await studioOp(home, 'auth', 'status');
    expect(status.code).toBe(0);
    expect(status.json.credential).toMatchObject({ name: 'operator-token', exists: true, mode: '600', ownerOnly: true, prefix: 'api' });
    expect(status.json.server).toMatchObject({ authenticated: true });
    expect(status.json.server.projects).toContain(project);
  });

  const green = await ownerCard('Green evidence card');
  const red = await ownerCard('Red evidence card');
  const missing = await ownerCard('Missing evidence card');
  writeFileSync(join(green.folderPath, 'remote-review-grade-e2e-green.md'), reviewReport('e2e-green', 'Pass', [
    "| build-tests | pass | CommandPassed | Review command 'verify-1' passed. |",
    '| requirement-fit | pass | RemoteAspectVerdict | Requirements match. |',
    '| code-quality | pass | RemoteAspectVerdict | Code quality passed. |']));
  // The content verdict the server's completion check reads: the local review
  // context names the card's current brief version and requirement-fit passed.
  const briefFile = join(green.folderPath, '.metadata', 'brief-version');
  const brief = existsSync(briefFile) ? readFileSync(briefFile, 'utf8').trim()
    : createHash('sha256').update(existsSync(join(green.folderPath, 'prompt.md'))
      ? readFileSync(join(green.folderPath, 'prompt.md'), 'utf8') : '').digest('hex');
  writeFileSync(join(green.folderPath, 'content-review-context.json'), JSON.stringify({ briefVersion: brief }));
  writeFileSync(join(green.folderPath, 'aspect-requirement-fit.json'), JSON.stringify({ status: 'pass',
    summary: 'The delivery matches the brief.', evidenceChecked: 'Fixture review for the studio-op end-to-end test.' }));
  // A card that delivers no code names its deliverable under results/.
  mkdirSync(join(green.folderPath, 'results'), { recursive: true });
  writeFileSync(join(green.folderPath, 'results', 'deliverables.md'), '# Deliverables\n\nFixture deliverable of the studio-op end-to-end test.\n');
  writeFileSync(join(red.folderPath, 'remote-review-grade-e2e-red.md'), reviewReport('e2e-red', 'ProductFailure', [
    "| build-tests | pass | CommandPassed | Review command 'verify-1' passed. |",
    '| code-quality | block | RemoteAspectVerdict | The parser change drops an error path. |']));

  await test.step('watch: one watcher at a time, and it wakes on the first decision', async () => {
    await ownerMove(red, '5-human-review');
    await ownerMove(missing, '5-human-review');
    // Their Human Review events are published before the watcher starts at the head.
    await until(async () => {
      const events = await ownerDecisions();
      return [red.key, missing.key].every(key =>
        events.some(event => event.type === 'human-review.ready' && event.cardKey === key));
    }, 'the Human Review events of the fixture cards');

    const first = start(['watch', '--session', 'e2e-first', '--timeout', '120', '--json'], home);
    await until(() => /Watching the decision stream/.test(first.stderr()), 'the first watcher');
    const remote = await studioOp(otherMachine, 'watch', '--session', 'e2e-second', '--timeout', '30');
    expect(remote.code, remote.stdout + remote.stderr).toBe(9);
    expect(remote.json).toMatchObject({ error: 'watcher-active', scope: 'server-lease', session: 'e2e-first' });
    const local = await studioOp(home, 'watch', '--timeout', '30');
    expect(local.code).toBe(9);
    expect(local.json).toMatchObject({ error: 'watcher-active', scope: 'local-lock' });

    await ownerMove(green, '5-human-review');
    const woke = await first.done;
    expect(woke.code, woke.stdout + woke.stderr).toBe(21);
    expect(woke.json.event).toMatchObject({ type: 'human-review.ready', cardKey: green.key, project });
    const lease = await owner.get('/api/operator/watcher/lease');
    expect((await lease.json()).active).toBe(false);
  });

  await test.step('accept refuses red and missing evidence and accepts the green card', async () => {
    const refusedRed = await studioOp(home, 'accept', red.key);
    expect(refusedRed.code, refusedRed.stdout).toBe(7);
    // Studio may also park a red card for an operator decision; the evidence reason must be named either way.
    expect(refusedRed.json.reasons.map((reason: any) => reason.code), refusedRed.stdout).toContain('evidence-red');
    const refusedMissing = await studioOp(home, 'accept', missing.key);
    expect(refusedMissing.code, refusedMissing.stdout).toBe(7);
    expect(refusedMissing.json.reasons.map((reason: any) => reason.code), refusedMissing.stdout).toContain('evidence-missing');
    expect((await ownerRead(red)).state).toBe('5-human-review');
    expect((await ownerRead(missing)).state).toBe('5-human-review');

    const accepted = await studioOp(home, 'accept', green.key, '--reason', 'End-to-end proof.');
    expect(accepted.code, accepted.stdout + accepted.stderr).toBe(0);
    expect(accepted.json.evidence).toMatchObject({ attemptId: 'e2e-green', buildTests: 'passed' });
    expect(accepted.json.logError).toBeNull();
    await until(async () => ['6-completed', '7-archive'].includes((await ownerRead(green)).state), 'the accepted lane');
  });

  await test.step('continue, create in Ready, title repair and lane moves', async () => {
    // A networked Studio runs rounds on a Runner only; the round queues for its claim.
    const remote = await owner.put(`/api/projects/${encodeURIComponent(project)}/execution-runner`,
      { data: { executionLocation: 'class:linux', pickupMode: 'manual' } });
    expect(remote.ok(), await remote.text()).toBeTruthy();
    const continued = await studioOp(home, 'continue', missing.key, '--prompt-file', promptFile,
      '--reason', 'Review evidence is missing.');
    expect(continued.code, continued.stdout + continued.stderr).toBe(0);

    const createdCard = await studioOp(home, 'create', '--ready', '--project', project, '--title', 'Follow-up from operator triage',
      '--prompt-file', promptFile);
    expect(createdCard.code, createdCard.stdout + createdCard.stderr).toBe(0);
    const newId = createdCard.json.answer.id as string;
    const fresh = await ownerRead({ id: newId });
    expect(fresh.state).toBe('2-ready');
    const key = fresh.key as string;

    const titled = await studioOp(home, 'title', key, 'Follow-up from operator triage (repaired)');
    expect(titled.code, titled.stdout + titled.stderr).toBe(0);
    expect((await ownerRead({ id: newId })).title).toBe('Follow-up from operator triage (repaired)');

    const moved = await studioOp(home, 'move', key, '0-backlog');
    expect(moved.code, moved.stdout + moved.stderr).toBe(0);
    expect((await ownerRead({ id: newId })).state).toBe('0-backlog');
    const refused = await studioOp(home, 'move', key, '6-completed');
    expect(refused.code).toBe(2);
    expect(refused.json.error).toBe('use-accept');

    const ejected = await studioOp(home, 'eject', key, '--project', project);
    // The card is in no batch flight: the batch gate route answers that it is not a member.
    expect(ejected.code, ejected.stdout).toBe(5);
    expect(ejected.json.status).toBe(404);
  });

  await test.step('triage, capacity and routing reads', async () => {
    const triage = await studioOp(home, 'triage');
    expect(triage.code, triage.stdout + triage.stderr).toBe(0);
    expect(triage.json.projects.map((entry: any) => entry.project)).toContain(project);
    const capacity = await studioOp(home, 'capacity');
    expect(capacity.code, capacity.stdout + capacity.stderr).toBe(0);
    expect(Array.isArray(capacity.json.quota)).toBe(true);
    const routing = await studioOp(home, 'routing');
    expect(routing.code, routing.stdout + routing.stderr).toBe(0);
    expect(routing.json.centralPolicyStatus).toBe(200);
  });

  await test.step('routing set-step changes the review step model of every project and keeps its other settings', async () => {
    const step = 'aspect-code-quality';
    const prepared = await owner.put(`/api/projects/${encodeURIComponent(project)}/pipeline-step`,
      { data: { pipelineType: 'task', stepId: step, mode: 'warn' } });
    expect(prepared.ok(), await prepared.text()).toBeTruthy();
    const changed = await studioOp(home, 'routing', 'set-step', '--step', step, '--cli', 'codex', '--model', 'gpt-5.5', '--all');
    expect(changed.code, changed.stdout + changed.stderr).toBe(0);
    expect(changed.json.results).toEqual([expect.objectContaining({ project, ok: true })]);
    const settings = await (await owner.get('/api/projects/settings')).json();
    const setting = settings[project].pipelineStepsByType.task[step];
    expect(setting).toMatchObject({ cliType: 'codex', model: 'gpt-5.5', mode: 'warn' });
  });

  await test.step('preflight and the server-first deploy plan', async () => {
    for (const operation of ['restart', 'deploy']) {
      const preflight = await studioOp(home, 'preflight', operation);
      expect(preflight.code, preflight.stdout + preflight.stderr).toBe(0);
      expect(preflight.json.allowed).toBe(true);
    }
    const plan = await studioOp(home, 'deploy', 'plan', '--target-version', '9.9.9', '--window', '15');
    expect(plan.code, plan.stdout + plan.stderr).toBe(0);
    expect(plan.json.steps.map((step: any) => step.kind)).toEqual(['drain', 'task-server', 'readiness', 'engine', 'hosts']);
  });

  await test.step('the operator log: append and read back', async () => {
    const appended = await studioOp(home, 'log', 'append', '--project', project, '--kind', 'decided',
      '--text', 'Left the red card with the Owner.', '--card', red.key);
    expect(appended.code, appended.stdout + appended.stderr).toBe(0);
    const read = await studioOp(home, 'log', '--project', project);
    expect(read.code).toBe(0);
    const texts = read.json.entries.map((entry: any) => entry.text);
    expect(texts).toContain('Left the red card with the Owner.');
    expect(texts.some((text: string) => text.includes(`Accepted ${green.key} as the operator verdict`))).toBe(true);
  });

  // No command printed the token, on success or refusal.
  const transcript = runs.map(run => [
    `$ studio-op ${run.args.join(' ')}`, `exit ${run.code}`, '--- stdout', run.stdout.trim(), '--- stderr', run.stderr.trim(), '',
  ].join('\n')).join('\n');
  writeFileSync(join(resultDir, 'transcript.md'),
    `# studio-op end-to-end transcript\n\nPrebuilt networked stack, operator API token from a file. ${runs.length} invocations.\n\n\`\`\`text\n${transcript}\n\`\`\`\n`);
  expect(runs.length).toBeGreaterThan(20);
  for (const run of runs) {
    expect(run.stdout.includes(token), `stdout of studio-op ${run.args.join(' ')}`).toBe(false);
    expect(run.stderr.includes(token), `stderr of studio-op ${run.args.join(' ')}`).toBe(false);
  }
  expect(transcript.includes(token)).toBe(false);
  await owner.dispose();
  await anonymous.dispose();
});
