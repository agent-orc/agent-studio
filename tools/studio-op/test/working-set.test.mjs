// Real HTTP fake, isolated per test. It rejects writes without both lease and current revision.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { main } from '../src/cli.mjs';
import { fakeToken, reviewProjection } from './fake-studio.mjs';

async function fixture(t) {
  const token = fakeToken();
  const home = fs.mkdtempSync(path.join(os.tmpdir(), 'studio-working-set-'));
  const tokenFile = path.join(home, 'credential');
  fs.writeFileSync(tokenFile, token, { mode: 0o600 });
  const prompt = path.join(home, 'prompt.md');
  fs.writeFileSync(prompt, 'Fix the parser.\n');
  const cards = new Map([
    ['DEMO-1', { key: 'DEMO-1', id: 'one', projectName: 'Demo', state: '2-ready', order: 2, cliType: 'codex', model: 'test-model', thinkingLevel: 'high', references: { dependsOn: ['DEMO-2'], relatedTo: ['DEMO-3'] } }],
    ['DEMO-2', { key: 'DEMO-2', id: 'two', projectName: 'Demo', state: '3-progress', cliType: 'claude', references: { dependsOn: [] } }],
    ['DEMO-3', { key: 'DEMO-3', id: 'three', projectName: 'Demo', state: '5-human-review', orchestratorVerdict: 'accept', reviewProjection: reviewProjection(), references: { dependsOn: [] }, integration: { status: 'integrated' } }],
    ['DEMO-4', { key: 'DEMO-4', id: 'four', projectName: 'Demo', state: '5e-escalated', parkedBlocker: { parkedAt: '2026-11-25T10:00:00Z', reason: 'Need clarification.\nSecond line.' }, references: { dependsOn: [] } }],
    ['DEMO-5', { key: 'DEMO-5', id: 'five', projectName: 'Demo', state: '2-ready', order: 1, model: 'other-model', thinkingLevel: 'low', references: { dependsOn: ['DEMO-3'] } }],
  ]);
  const requests = [];
  const revisions = new Map();
  const leases = new Set();
  const state = { cards, requests, refuse: null, stale: false, blocked: true, boardReads: 0, mutateAfterBaseline: false, red: false, watcher: false, heartbeatSeconds: 30, loseWatcher: false };
  const refusal = 'Completion refused: ' + 'The delivery has not reached the integration branch. '.repeat(20) + 'Final refusal detail.';
  const server = http.createServer(async (req, res) => {
    const url = new URL(req.url, 'http://localhost');
    const route = url.pathname;
    let text = '';
    for await (const chunk of req) text += chunk;
    const body = text ? JSON.parse(text) : undefined;
    requests.push({ method: req.method, route, query: url.search, body, ifMatch: req.headers['if-match'], auth: req.headers.authorization });
    const send = (status, answer) => { res.writeHead(status, { 'Content-Type': 'application/json' }); res.end(JSON.stringify(answer)); };
    if (req.headers.authorization !== `Bearer ${token}`) return send(401, { error: 'unauthenticated' });
    if (route === '/api/watch-paths') return send(200, [{ name: 'Demo', path: '/resolved/demo', rootPath: '/wrong/repository' }]);
    if (route === '/api/tasks/archive') return send(200, { total: 12, items: [] });
    if (route === '/api/tasks/grouped') {
      state.boardReads++;
      if (state.mutateAfterBaseline && state.boardReads === 2) {
        cards.get('DEMO-1').state = '5e-escalated';
        cards.get('DEMO-1').parkedBlocker = { reason: 'Escalated now', parkedAt: '2026-11-27T10:00:00Z' };
        state.red = true;
      }
      const values = [...cards.values()].filter(card => card.state !== '7-archive');
      const laneNames = { '2-ready': 'ready', '3-progress': 'progress', '5-human-review': 'humanReview', '5e-escalated': 'escalated' };
      const grouped = Object.groupBy(values.sort((a, b) => (a.order ?? 999) - (b.order ?? 999)), card => laneNames[card.state] ?? card.state);
      grouped.review = values.filter(card => card.state === '4-auto-review');
      return send(200, grouped);
    }
    if (route === '/api/operator/batch-triage') return send(200, { projects: [{ project: 'Demo',
      flights: [{ batchId: 'batch-1', phase: state.red ? 'red' : 'running', phaseReason: state.red ? 'Tests failed' : null }],
      members: state.red ? [{ taskKey: 'DEMO-5', red: true, status: 'red' }] : [] }] });
    if (route === '/api/projects/Demo/batch-gate/state') return send(200, { gateSlots: { busy: 1, total: 2 }, pending: [{ taskKey: 'DEMO-3' }] });
    if (route === '/api/operator/capacity') return send(200, { quota: [{ cliType: 'codex', fresh: true,
      snapshot: { subscriptionKey: 'subscription-1', windows: [{ label: 'weekly', usedPct: 64, resetAt: '2026-11-28T00:00:00Z' }] } }] });
    if (route === '/api/operator/coding-capacity') return send(200, {
      fleet: {}, projects: [{ project: 'Demo', totalSlots: 1, usedSlots: state.blocked ? 1 : 0, freeSlots: state.blocked ? 0 : 1,
        cards: [{ cardKey: 'DEMO-1', blocks: state.blocked ? [
          { class: 'dependency', reason: 'waits for DEMO-2', dependency: 'DEMO-2' },
          { class: 'dispatch-rule', reason: 'Last observed claim refusal: route denied' },
          { class: 'quota', reason: 'Wait until reset' }] : [] }] }],
      subscriptions: [{ cliType: 'codex', subscriptionKey: 'subscription-1', window: 'weekly', usedPercent: 64, resetAt: '2026-11-28T00:00:00Z', status: 'fresh' }],
    });
    if (route === '/api/operator/watcher/lease' && req.method === 'POST') {
      state.watcher = true;
      return send(200, { lease: { leaseId: 'watch-1', heartbeatSeconds: state.heartbeatSeconds } });
    }
    if (route === '/api/operator/watcher/lease/watch-1/heartbeat') return state.loseWatcher
      ? send(409, { error: 'watcher-lease-lost', holder: 'owner' }) : send(200, {});
    if (route === '/api/operator/watcher/lease/watch-1' && req.method === 'DELETE') { state.watcher = false; return send(200, {}); }
    if (route === '/api/operator/log') return send(200, {});
    if (route === '/api/echo') return send(200, { method: req.method, body, echoed: token });
    if ((route === '/api/tasks' || route === '/api/tasks/') && req.method === 'POST') {
      const key = `DEMO-${cards.size + 1}`;
      const card = { key, id: `created-${cards.size}`, projectName: 'Demo', state: body.targetState,
        title: body.title, mode: body.mode, cliType: body.cliType, model: body.model, thinkingLevel: body.thinkingLevel, references: {} };
      cards.set(key, card);
      return send(200, { id: card.id, taskKey: key });
    }
    const match = route.match(/^\/api\/tasks\/([^/]+)(\/.*)?$/);
    if (match) {
      const key = decodeURIComponent(match[1]);
      const card = cards.get(key);
      const suffix = match[2] ?? '';
      if (!card) return send(404, { error: 'not-found' });
      if (req.method === 'GET' && !suffix) return send(200, { info: card });
      if (req.method === 'GET' && suffix === '/review-projection') return send(200, reviewProjection());
      const revision = revisions.get(key) ?? 1;
      if (suffix === '/attention') {
        if (state.refuse === 'attention') return send(409, { error: 'attention-held', holder: 'owner' });
        leases.add(key);
        return send(200, { lease: { holder: 'fake-operator' }, revision: `"${revision}"` });
      }
      if (!leases.has(key)) return send(409, { error: 'attention-lease-required' });
      if (!req.headers['if-match']) return send(428, { error: 'card-revision-required' });
      if (state.stale || req.headers['if-match'] !== `"${revision}"`) return send(412, { error: 'stale-card-revision' });
      if (state.refuse === key && suffix === '/move') return send(409, { error: refusal, context: 'integration' });
      revisions.set(key, revision + 1);
      leases.delete(key);
      if (suffix === '/move') {
        if (body.targetState === '2-ready' && body.targetIndex === 0) {
          for (const other of cards.values()) if (other.state === '2-ready') other.order = (other.order ?? 0) + 1;
          card.order = 0;
        }
        card.state = body.targetState;
      }
      if (suffix === '/continue') { card.state = '2-ready'; return send(202, { status: 'queued' }); }
      if (suffix === '/references') card.references = body;
      if (suffix === '/model') card.model = body.model;
      if (suffix === '/cli-type') card.cliType = body.cliType;
      if (suffix === '/thinking-level') card.thinkingLevel = body.thinkingLevel;
      return send(200, { updated: true, echoed: token });
    }
    return send(404, { error: 'not-found' });
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const url = `http://127.0.0.1:${server.address().port}`;
  t.after(async () => { server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); fs.rmSync(home, { recursive: true, force: true }); });
  async function run(args, { json = true, env = {}, config = false } = {}) {
    let stdout = '', stderr = '';
    const code = await main([...args, ...(json ? ['--json'] : []), ...(config ? [] : ['--token-file', tokenFile])], {
      home, env: { STUDIO_OP_URL: url, ...env }, stdout: { write: value => { stdout += value; } }, stderr: { write: value => { stderr += value; } },
    });
    assert.ok(!stdout.includes(token) && !stderr.includes(token), 'no output exposes the loaded token');
    return { code, stdout, stderr, data: json && stdout && args[0] !== 'watch' ? JSON.parse(stdout) : null };
  }
  return { ...state, state, run, home, token, tokenFile, prompt, url, refusal };
}

for (const json of [true, false]) test(`working-set commands have ${json ? 'JSON' : 'compact text'} output and enforce each card fence`, async t => {
  const f = await fixture(t);
  const file = path.join(f.home, 'cards.json');
  fs.writeFileSync(file, JSON.stringify([
    { id: 'first', title: 'First', project: 'Demo', mode: 'coding', type: 'feature', level: 'high', cli: 'codex', model: 'demo-model', prompt: 'Implement one.' },
    { id: 'second', title: 'Second', project: 'Demo', dependsOn: ['first'] },
  ]));
  const commands = [['snap'], ['card', 'DEMO-1', 'DEMO-3'], ['ready'], ['deps', 'ready'],
    ['continue', 'DEMO-4', f.prompt, 'high', 'codex', 'demo-model'], ['move', 'humanReview', 'DEMO-1', 'DEMO-2'],
    ['top', 'DEMO-1', 'DEMO-2'], ['route', 'codex', 'new-model', 'low', 'DEMO-1', 'DEMO-2'],
    ['depends', 'DEMO-1', 'DEMO-3'], ['accept', 'DEMO-3'], ['accept-with-reason', 'DEMO-4', 'A code-free delivery accepted by the authorized owner.'],
    ['create', file], ['quota'], ['why', 'DEMO-1'], ['get', '/api/echo'], ['post', '/api/echo', '{"value":1}'],
    ['put', '/api/echo', '{"value":2}'], ['put', '/api/tasks/DEMO-1/model?project=Demo', '{"model":"raw-model"}'],
    ['post', '/api/tasks/DEMO-1/move?project=Demo', '{"targetState":"2-ready"}']];
  for (const args of commands) {
    const output = await f.run(args, { json });
    assert.equal(output.code, 0, `${args[0]}: ${output.stdout}${output.stderr}`);
    assert.ok(json ? output.data.ok : output.stdout.trim().length > 0, `${args[0]} has output`);
    if (!json && args[0] === 'card') assert.match(output.stdout, /integration=integrated/);
  }
  assert.ok(f.requests.every(req => req.auth === `Bearer ${f.token}`));
  const continued = f.requests.find(req => req.route.endsWith('/continue'));
  assert.deepEqual(continued.body, { prompt: 'Fix the parser.\n', mode: 'continue', model: 'demo-model', cliType: 'codex', thinkingLevel: 'high' });
  const create = f.requests.filter(req => req.route === '/api/tasks/' && req.method === 'POST');
  assert.ok(create.every(req => req.body.targetState === '0-backlog' && req.body.watchPath === '/resolved/demo'));
  assert.equal(create[0].body.mode, 'coding'); assert.equal(create[0].body.thinkingLevel, 'high');
  assert.equal(create[0].body.model, 'demo-model'); assert.equal(create[0].body.promptMarkdown, 'Implement one.');
  const edges = f.requests.find(req => req.route === '/api/tasks/DEMO-7/references');
  assert.deepEqual(edges.body.dependsOn, ['DEMO-6']);
  const lastReferences = f.requests.findLastIndex(req => /DEMO-[67]\/references$/.test(req.route));
  const firstPromotion = f.requests.findIndex(req => /DEMO-[67]\/move$/.test(req.route));
  assert.ok(lastReferences < firstPromotion, 'all dependencies precede the first Ready move');
  assert.deepEqual([...f.cards.values()].filter(card => card.state === '2-ready').sort((a, b) => a.order - b.order).slice(0, 2).map(card => card.key), ['DEMO-6', 'DEMO-7']);
  const referenceWrite = f.requests.find(req => req.route === '/api/tasks/DEMO-1/references');
  assert.deepEqual(referenceWrite.body.relatedTo, ['DEMO-3'], 'depends preserves other relations');
  const writes = f.requests.filter(req => req.method !== 'GET' && /^\/api\/tasks\/[^/]+\//.test(req.route) && !req.route.endsWith('/attention'));
  for (const write of writes) assert.match(write.ifMatch, /^"\d+"$/, `${write.route} has the attention revision`);
});

test('snap renders lane counts, running CLI, dated escalation first line, accept verdict, slots, pending work and batches', async t => {
  const f = await fixture(t);
  const value = await f.run(['snap']);
  assert.equal(value.data.counts['2-ready'], 2);
  assert.equal(value.data.counts['7-archive'], 12);
  assert.equal(value.data.progress[0].cli, 'claude');
  assert.deepEqual(value.data.escalated[0], { key: 'DEMO-4', date: '2026-11-25T10:00:00Z', reason: 'Need clarification.' });
  assert.equal(value.data.acceptedReview[0].key, 'DEMO-3');
  assert.deepEqual(value.data.gates[0].slots, { busy: 1, total: 2 });
  assert.equal(value.data.gates[0].pending.length, 1); assert.equal(value.data.batches[0].batchId, 'batch-1');
  assert.deepEqual((await f.run(['quota'])).data.subscriptions[0], { cliType: 'codex', subscriptionKey: 'subscription-1', window: 'weekly', usedPercent: 64, resetAt: '2026-11-28T00:00:00Z', status: 'fresh' });
  assert.deepEqual((await f.run(['ready'])).data.cards.map(card => card.key), ['DEMO-5', 'DEMO-1']);
  assert.deepEqual((await f.run(['deps', 'ready'])).data.edges.map(edge => edge.predecessorLane), ['5-human-review', '3-progress']);
});

test('why reports all published blocks and project slot exhaustion, and admits unknown scheduling causes', async t => {
  const f = await fixture(t);
  const blocked = await f.run(['why', 'DEMO-1']);
  assert.deepEqual(blocked.data.reasons.map(reason => reason.class), ['dependency', 'dispatch-rule', 'quota', 'slot']);
  f.state.blocked = false;
  assert.equal((await f.run(['why', 'DEMO-1'])).data.reasons[0].class, 'unexplained');
  assert.equal((await f.run(['why', 'DEMO-5'])).data.reasons[0].class, 'unknown');
  assert.equal((await f.run(['why', 'DEMO-2'])).data.lane, '3-progress');
});

test('server refusal preserves the full text on stderr and exit 4, including JSON mode', async t => {
  const f = await fixture(t);
  f.state.refuse = 'DEMO-3';
  for (const json of [true, false]) {
    const output = await f.run(['accept', 'DEMO-3'], { json });
    assert.equal(output.code, 4);
    assert.ok(output.stderr.includes(f.refusal));
    assert.equal(f.cards.get('DEMO-3').state, '5-human-review');
  }
  f.state.refuse = 'DEMO-4';
  assert.equal((await f.run(['accept-with-reason', 'DEMO-4', 'An explicit owner reason for completion.'])).code, 4);
});

test('not found is 3; attention-held and stale revisions are 4 and never blindly retry', async t => {
  const f = await fixture(t);
  assert.equal((await f.run(['card', 'MISSING'])).code, 3);
  assert.equal((await f.run(['move', 'ready', 'MISSING'])).code, 3);
  f.state.refuse = 'attention';
  const held = await f.run(['route', 'codex', 'demo-model', 'high', 'DEMO-1']);
  assert.equal(held.code, 4); assert.match(held.stderr, /attention-held/);
  assert.ok(!f.requests.some(req => req.route.endsWith('/cli-type')));
  f.state.refuse = null; f.state.stale = true;
  const stale = await f.run(['put', '/api/tasks/DEMO-1/model', '{"model":"new"}']);
  assert.equal(stale.code, 4); assert.match(stale.stderr, /stale-card-revision/);
  assert.equal(f.requests.filter(req => req.route.endsWith('/model')).length, 1);
});

test('top preserves requested order; bulk acceptance and moves report completed items on partial failure', async t => {
  const f = await fixture(t);
  await f.run(['top', 'DEMO-2', 'DEMO-1', 'DEMO-5']);
  assert.deepEqual([...f.cards.values()].filter(card => card.state === '2-ready').sort((a, b) => a.order - b.order).map(card => card.key), ['DEMO-2', 'DEMO-1', 'DEMO-5']);
  const moved = await f.run(['move', 'humanReview', 'DEMO-2', 'MISSING', 'DEMO-1']);
  assert.equal(moved.code, 3); assert.equal(moved.data.answer.completed[0].key, 'DEMO-2');
  f.cards.get('DEMO-2').reviewProjection = reviewProjection();
  const accepted = await f.run(['accept', 'DEMO-2', 'DEMO-3']);
  assert.equal(accepted.code, 0); assert.equal(accepted.data.items.length, 2);
});

test('bulk create validates local references and cycles before any writes and reports partial create keys', async t => {
  const f = await fixture(t);
  const file = path.join(f.home, 'invalid.json');
  for (const cards of [
    [{ id: 'a', title: 'A', project: 'Demo', dependsOn: ['missing'] }],
    [{ id: 'a', title: 'A', project: 'Demo', dependsOn: ['a'] }],
    [{ id: 'a', title: 'A', project: 'Demo' }, { id: 'a', title: 'B', project: 'Demo' }],
  ]) {
    fs.writeFileSync(file, JSON.stringify(cards));
    assert.equal((await f.run(['create', file])).code, 2);
  }
  assert.equal(f.requests.length, 0);
  fs.writeFileSync(file, JSON.stringify([{ id: 'a', title: 'A', project: 'Demo' }]));
  f.state.refuse = 'attention';
  const partial = await f.run(['create', file]);
  assert.equal(partial.code, 4); assert.equal(partial.data.answer.created[0].key, 'DEMO-6');
  assert.equal(f.cards.get('DEMO-6').state, '0-backlog');
});

test('origin and token configuration support env, explicit file, and config-relative file without writing tokens', async t => {
  const f = await fixture(t);
  const config = path.join(f.home, 'connection.json');
  fs.writeFileSync(config, JSON.stringify({ url: f.url, tokenFile: 'credential' }));
  assert.equal((await f.run(['quota'], { config: true, env: { STUDIO_OP_TOKEN: f.token } })).code, 0);
  assert.equal((await f.run(['quota'], { config: true, env: { AGENT_STUDIO_API_TOKEN: f.token } })).code, 0);
  assert.equal((await f.run(['quota', '--config', config], { config: true })).code, 0);
  assert.equal(fs.readFileSync(f.tokenFile, 'utf8'), f.token);
  assert.deepEqual(fs.readdirSync(f.home).sort(), ['connection.json', 'credential', 'prompt.md']);
  const invalid = await f.run(['post', 'https://elsewhere.invalid/api/echo', '{}']);
  assert.equal(invalid.code, 2); assert.match(invalid.data.message, /API path/);
});

for (const json of [true, false]) test(`watch emits each observed lane/escalation/red gate transition once (${json ? 'NDJSON' : 'text'}) and cleans up`, async t => {
  const f = await fixture(t);
  f.state.mutateAfterBaseline = true;
  const watched = await f.run(['watch', '--interval', '1', '--timeout', '3'], { json });
  if (json) {
    const events = watched.stdout.trim().split('\n').map(line => JSON.parse(line));
    assert.deepEqual(events.map(event => event.type), ['lane.changed', 'escalation.opened', 'gate.red', 'gate.red']);
    assert.equal(events[0].from, '2-ready'); assert.equal(events[0].to, '5e-escalated');
  }
  assert.equal(watched.code, 0);
  assert.equal(watched.stdout.split('\n').filter(line => line.includes('lane.changed')).length, 1);
  assert.equal(watched.stdout.split('\n').filter(line => line.includes('escalation.opened')).length, 1);
  assert.equal(watched.stdout.split('\n').filter(line => line.includes('gate.red')).length, 2);
  assert.equal(f.state.watcher, false);
  assert.ok(!fs.existsSync(path.join(f.home, '.config', 'agent-studio', 'studio-op-watch.lock')));
});


test('lane aliases match the existing server TaskStates contract', async t => {
  const f = await fixture(t);
  const source = fs.readFileSync(new URL('../../../contracts/TaskServer.Contracts/TaskStates.cs', import.meta.url), 'utf8');
  for (const name of ['Backlog', 'Preparation', 'OrchestratorPrep', 'Ready', 'Progress', 'FailedPickup', 'CodeNotComplete', 'AutoReview', 'HumanReview', 'Escalated']) {
    const canonical = source.match(new RegExp(`const string ${name} = "([^\"]+)"`))[1];
    const value = await f.run(['move', name, 'DEMO-1']);
    assert.equal(value.code, 0, value.stderr);
    assert.equal(f.cards.get('DEMO-1').state, canonical);
  }
});

test('deps resolves archived predecessors through card detail, outside the board snapshot', async t => {
  const f = await fixture(t);
  f.cards.get('DEMO-3').state = '7-archive';
  const value = await f.run(['deps', 'ready']);
  assert.equal(value.code, 0);
  assert.equal(value.data.edges.find(edge => edge.predecessor === 'DEMO-3').predecessorLane, '7-archive');
});


test('continuous watch heartbeats and releases its lease on server refusal', async t => {
  const f = await fixture(t);
  f.state.heartbeatSeconds = 2;
  f.state.loseWatcher = true;
  f.state.mutateAfterBaseline = true;
  const value = await f.run(['watch', '--interval', '1', '--timeout', '5']);
  assert.equal(value.code, 4);
  assert.match(value.stderr, /watcher-lease-lost/);
  const lines = value.stdout.trim().split('\n').map(line => JSON.parse(line));
  assert.equal(lines[0].type, 'lane.changed');
  assert.equal(lines.at(-1).error, 'watcher-lease-lost');
  assert.equal(f.state.watcher, false);
  assert.equal(f.requests.filter(req => req.route.endsWith('/heartbeat')).length, 1);
  assert.ok(!fs.existsSync(path.join(f.home, '.config', 'agent-studio', 'studio-op-watch.lock')));
});
