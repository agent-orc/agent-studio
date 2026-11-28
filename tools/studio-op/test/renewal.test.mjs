import test from 'node:test';
import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import net from 'node:net';
import { agentCall, serveAgent, digest, keyThumbprint } from '../src/agent.mjs';
import { checkSocketPath, createSocketPath } from '../src/agent-socket.mjs';

const decode = part => JSON.parse(Buffer.from(part, 'base64url'));
const verify = (jwk, data, signature) => crypto.verify('sha256', Buffer.from(data),
  { key: crypto.createPublicKey({ key: jwk, format: 'jwk' }), dsaEncoding: 'ieee-p1363' }, Buffer.from(signature, 'base64url'));

for (const refused of [false, true]) test(`resident rotates with the retained device key or asks for login after refusal (${refused})`, async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'studio-op-renewal-'));
  const token = ['oas', crypto.randomUUID(), crypto.randomBytes(32).toString('base64url')].join('.');
  fs.writeFileSync(path.join(root, 'operator-session'), token);
  let time = Date.now();
  let renewal;
  let settle;
  const observed = new Promise(resolve => { settle = resolve; });
  const resident = await serveAgent(root, () => {}, { now: () => time, monitorIntervalMs: 20,
    fetchImpl: async (url, options) => {
      if (String(url).endsWith('/renew')) {
        renewal = { body: JSON.parse(options.body), proof: options.headers.DPoP };
        settle();
        return Response.json(refused ? { error: 'renewal-device-key-missing' }
          : { keyExpiresAt: new Date(time + 3600_000).toISOString() }, { status: refused ? 401 : 200 });
      }
      return Response.json({ state: 'active' });
    } });
  try {
    await agentCall(resident.endpoint, { operation: 'attach', token, sessionId: 'session', baseUrl: 'https://studio.test/',
      expiresAt: new Date(time + 24 * 3600_000).toISOString(), keyExpiresAt: new Date(time + 3600_000).toISOString() });
    time += 50 * 60_000;
    await Promise.race([observed, new Promise((_, reject) => setTimeout(() => reject(new Error('No rotation.')), 3000).unref())]);
    assert.equal(renewal.body.sessionId, 'session');
    assert.notEqual(keyThumbprint(renewal.body.newPublicKey), resident.thumbprint);
    assert.ok(verify(resident.publicJwk, `studio-op-session-renewal-v1\nsession\n${keyThumbprint(renewal.body.newPublicKey)}\n${renewal.body.issuedAt}`, renewal.body.signature));
    const [h, p, signature] = renewal.proof.split('.');
    assert.deepEqual(decode(h).jwk, resident.publicJwk);
    assert.ok(verify(resident.publicJwk, `${h}.${p}`, signature));
    // Give the fetch continuation and key replacement one event-loop turn.
    await new Promise(resolve => setTimeout(resolve, 50));
    if (refused) {
      assert.match(fs.readFileSync(path.join(root, 'operator-session-ended'), 'utf8'), /studio-op login/);
      await assert.rejects(agentCall(resident.endpoint, { operation: 'sign', tokenHash: digest(token), method: 'GET', url: 'https://studio.test/api/tasks' }));
    } else {
      const { proof } = await agentCall(resident.endpoint, { operation: 'sign', tokenHash: digest(token), method: 'GET', url: 'https://studio.test/api/tasks' });
      assert.deepEqual(decode(proof.split('.')[0]).jwk, renewal.body.newPublicKey);
    }
  } finally { resident.close(); fs.rmSync(root, { recursive: true, force: true }); }
});

test('resident binds and cleans up with a 120-byte temp directory', { skip: process.platform === 'win32' }, async t => {
  const root = fs.mkdtempSync('/tmp/studio-op-test-');
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  const deep = path.join(root, 'd'.repeat(119 - Buffer.byteLength(root)));
  fs.mkdirSync(deep);
  const previous = { TMPDIR: process.env.TMPDIR, XDG_RUNTIME_DIR: process.env.XDG_RUNTIME_DIR };
  t.after(() => {
    for (const [name, value] of Object.entries(previous)) {
      if (value === undefined) delete process.env[name]; else process.env[name] = value;
    }
  });
  process.env.TMPDIR = deep;
  delete process.env.XDG_RUNTIME_DIR;
  assert.equal(Buffer.byteLength(os.tmpdir()), 120);
  const resident = await serveAgent(root);
  t.after(resident.close);
  const directory = path.dirname(resident.endpoint);
  assert.equal(path.dirname(directory), '/tmp');
  assert.equal(fs.lstatSync(directory).uid, process.getuid());
  assert.equal(fs.statSync(directory).mode & 0o777, 0o700);
  assert.equal(fs.statSync(resident.endpoint).mode & 0o777, 0o600);
  assert.deepEqual(await agentCall(resident.endpoint, { operation: 'stop' }), { stopped: true });
  resident.close();
  assert.equal(fs.existsSync(directory), false);
});

test('resident releases its directory and propagates a failed bind', { skip: process.platform === 'win32' }, async t => {
  let directory;
  const failure = new Error('bind refused by host');
  t.mock.method(net.Server.prototype, 'listen', function (endpoint) {
    directory = path.dirname(endpoint);
    process.nextTick(() => this.emit('error', failure));
    return this;
  });
  await assert.rejects(serveAgent(os.tmpdir()), error => error === failure);
  assert.equal(fs.existsSync(directory), false);
});

test('socket directory choice budgets the complete UTF-8 path', { skip: process.platform === 'win32' }, t => {
  const root = fs.mkdtempSync('/tmp/studio-op-choice-');
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  const runtime = path.join(root, 'runtime');
  const temp = path.join(root, 'temp');
  const deep = path.join(root, 'é'.repeat(60));
  for (const base of [runtime, temp, deep]) fs.mkdirSync(base, { mode: 0o700 });
  for (const [xdg, tmp, expected] of [[runtime, temp, runtime], [deep, temp, temp],
    [null, temp, temp], [deep, deep, '/tmp']]) {
    const endpoint = createSocketPath(root, xdg, tmp);
    const directory = path.dirname(endpoint);
    t.after(() => fs.rmSync(directory, { recursive: true, force: true }));
    assert.equal(path.dirname(directory), expected);
    checkSocketPath(endpoint);
  }
  assert.throws(() => checkSocketPath(deep), error =>
    error.message.includes(deep) && error.message.includes(process.platform === 'linux' ? '107' : '103'));
});
