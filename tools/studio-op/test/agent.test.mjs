import { test } from 'node:test';
import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fork } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { agentCall, digest, signFromAgent } from '../src/agent.mjs';
import { login } from '../src/login.mjs';
import { StudioClient } from '../src/studio.mjs';
import { fakeToken } from './fake-studio.mjs';

// Observe generated material only inside the test's memory. The production signer never exports it.
const hook = `import crypto from 'node:crypto';
const generate = crypto.generateKeyPairSync;
crypto.generateKeyPairSync = (...args) => {
 const pair = generate(...args);
 process.send?.({ observed: { d: pair.privateKey.export({format:'jwk'}).d,
  der: pair.privateKey.export({format:'der',type:'pkcs8'}).toString('base64') } });
 return pair;
};`;

async function instrumentedAgent(home, temp, observed) {
  const loader = path.join(temp, 'observe-key.mjs');
  fs.writeFileSync(loader, hook);
  const configDir = path.join(home, '.config', 'agent-studio');
  fs.mkdirSync(configDir, { recursive: true });
  const child = fork(fileURLToPath(new URL('../src/agent.mjs', import.meta.url)), [configDir], {
    env: { ...process.env, HOME: home, USERPROFILE: home, TMPDIR: temp, TMP: temp, TEMP: temp },
    execArgv: ['--import', loader], windowsHide: true, stdio: ['ignore', 'ignore', 'ignore', 'ipc'],
  });
  const exited = new Promise(resolve => child.once('exit', resolve));
  const info = await new Promise((resolve, reject) => {
    child.once('error', reject);
    child.on('message', message => {
      if (message.observed) observed.push(message.observed);
      else if (message.error) reject(new Error(message.error));
      else resolve(message);
    });
  });
  return { ...info, child, exited };
}

function filesUnder(root) {
  return fs.readdirSync(root, { withFileTypes: true }).flatMap(entry => {
    const file = path.join(root, entry.name);
    return entry.isDirectory() ? filesUnder(file) : entry.isFile() ? [file] : [];
  });
}

test('after login the memory key is absent from the agent user home and temp, commands sign and copies cannot', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'dpop-key-proof-'));
  const home = path.join(root, 'home'); const temp = path.join(root, 'temp');
  fs.mkdirSync(temp, { recursive: true });
  const observed = [];
  const resident = await instrumentedAgent(home, temp, observed);
  const token = fakeToken('oas');
  const base = 'https://studio.test/';
  const configDir = path.join(home, '.config', 'agent-studio');
  let polls = 0;
  const ctx = { configDir, config: {}, output: { note() {}, protect() {} }, client: {
    base: new URL(base), url: route => new URL(route, base),
    request: async () => ({ userCode: 'WDJB-MJHT', verificationUri: base, verificationUriComplete: base,
      pollHandle: fakeToken('osp'), interval: 1, expiresAt: new Date(Date.now() + 60_000).toISOString() }),
    send: async (method, route, options) => {
      assert.ok(options.headers.DPoP, 'poll has proof'); polls++;
      return { status: 200, body: { status: 'session', token, expiresAt: new Date(Date.now() + 60_000).toISOString(),
        actor: 'operator-agent:test/session:test', delegatedBy: 'human:owner' } };
    },
  } };
  try {
    await login(ctx, {}, { wait: async () => {}, agentFactory: async () => resident });
    assert.equal(polls, 1);
    assert.equal(observed.length, 1);
    const searched = [...filesUnder(home), ...filesUnder(temp)];
    const material = observed.flatMap(key => [Buffer.from(key.d), Buffer.from(key.d, 'base64url'),
      Buffer.from(key.der), Buffer.from(key.der, 'base64')]);
    for (const file of searched) {
      const data = fs.readFileSync(file);
      assert.ok(material.every(key => !data.includes(key)), `Private key material found in ${file}`);
    }
    if (process.platform !== 'win32') assert.equal(fs.statSync(resident.endpoint).mode & 0o777, 0o600);
    const proof = await signFromAgent(configDir, token, 'GET', 'https://studio.test/api/tasks?q=1');
    const [h, p, s] = proof.split('.');
    const header = JSON.parse(Buffer.from(h, 'base64url'));
    const payload = JSON.parse(Buffer.from(p, 'base64url'));
    assert.equal(payload.htu, 'https://studio.test/api/tasks');
    assert.equal(payload.ath, digest(token));
    assert.equal(crypto.verify('sha256', Buffer.from(`${h}.${p}`), {
      key: crypto.createPublicKey({ key: header.jwk, format: 'jwk' }), dsaEncoding: 'ieee-p1363',
    }, Buffer.from(s, 'base64url')), true);
    const originalFetch = globalThis.fetch;
    const sent = [];
    try {
      globalThis.fetch = async (url, options) => {
        sent.push({ url: String(url), options });
        return new Response('{}', { status: 200 });
      };
      const client = new StudioClient({ url: base, credential: { token }, clientId: 'test', configDir });
      await client.request('POST', '/api/tasks/test/move', { body: { targetState: 'ready' } });
      const stream = await client.stream('/api/operator/decisions/stream', { query: { after: '1' } });
      await stream.body.cancel();
      for (const request of sent) {
        assert.ok(request.options.headers.DPoP);
        assert.equal(request.options.redirect, 'manual');
        const signed = JSON.parse(Buffer.from(request.options.headers.DPoP.split('.')[1], 'base64url'));
        assert.equal(signed.htm, request.options.method ?? 'GET');
        assert.equal(signed.htu, request.url.split('?')[0]);
      }
      assert.equal(sent.length, 2);
    } finally { globalThis.fetch = originalFetch; }
    const next = await signFromAgent(configDir, token, 'GET', 'https://studio.test/api/tasks');
    assert.notEqual(JSON.parse(Buffer.from(next.split('.')[1], 'base64url')).jti, payload.jti);
    const otherUser = path.join(root, 'other-user'); fs.mkdirSync(otherUser);
    fs.copyFileSync(path.join(configDir, 'operator-session'), path.join(otherUser, 'operator-session'));
    assert.equal(await signFromAgent(otherUser, token, 'GET', 'https://studio.test/api/tasks'), null);
    assert.equal(await signFromAgent(configDir, token, 'GET', 'https://attacker.test/api/tasks'), null);
    if (process.env.JOB_RESULTS_DIR) fs.writeFileSync(path.join(process.env.JOB_RESULTS_DIR, 'memory-key-disk-scan.txt'),
      `After login, searched all ${searched.length} regular files in the isolated agent user home and temp. No private scalar (raw/base64url) or PKCS8 (raw/base64) was found. Socket mode 600 checked on POSIX; fresh proofs verify cryptographically.\n`);
    await agentCall(resident.endpoint, { operation: 'stop' });
    await resident.exited;
    assert.equal(await signFromAgent(configDir, token, 'GET', 'https://studio.test/api/tasks'), null);
  } finally {
    resident.child.kill(); await resident.exited;
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('removing the session file ends its resident and removes its socket', { timeout: 15_000 }, async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'dpop-agent-end-'));
  const home = path.join(root, 'home'); const temp = path.join(root, 'temp');
  fs.mkdirSync(temp, { recursive: true });
  const resident = await instrumentedAgent(home, temp, []);
  const file = path.join(home, '.config', 'agent-studio', 'operator-session');
  const token = fakeToken('oas');
  try {
    fs.writeFileSync(file, token, { mode: 0o600 });
    await agentCall(resident.endpoint, { operation: 'attach', token, baseUrl: 'https://studio.test/', expiresAt: new Date(Date.now() + 60_000).toISOString() });
    fs.rmSync(file);
    await resident.exited;
    if (process.platform !== 'win32') assert.equal(fs.existsSync(resident.endpoint), false);
  } finally { resident.child.kill(); await resident.exited; fs.rmSync(root, { recursive: true, force: true }); }
});
