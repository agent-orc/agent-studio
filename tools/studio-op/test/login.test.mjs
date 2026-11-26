// Run: node --test tools/studio-op/test/login.test.mjs
//
// ES-3 (Dossier operator-elevated-session): studio-op login starts a terminal
// sign-in with the device's public key, prints the code, the link and a QR
// code, polls at the interval Studio names, and writes the session into the
// operator-session file with mode 600 without ever printing it.
import { test, after } from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import fs from 'node:fs';
import http from 'node:http';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { fakeToken } from './fake-studio.mjs';
import { keyThumbprint } from '../src/login.mjs';
import { stopAgent } from '../src/agent.mjs';

const bin = path.join(path.dirname(fileURLToPath(import.meta.url)), '..', 'studio-op.mjs');
const posix = process.platform !== 'win32';
const servers = [];
const homes = [];

after(async () => {
  for (const home of homes) { await stopAgent(path.join(home, '.config', 'agent-studio')); fs.rmSync(home, { recursive: true, force: true }); }
  for (const server of servers) server.close();
});

/** A Studio that answers one sign-in: the start, then the scripted poll answers in order. */
async function signInStudio(polls) {
  const requests = [];
  const server = http.createServer((req, res) => {
    let body = '';
    req.on('data', chunk => { body += chunk; });
    req.on('end', () => {
      const parsed = body ? JSON.parse(body) : null;
      requests.push({ method: req.method, path: req.url, authorization: req.headers.authorization ?? null, proof: req.headers.dpop ?? null, body: parsed });
      const send = (status, payload) => { res.writeHead(status, { 'Content-Type': 'application/json' }); res.end(JSON.stringify(payload)); };
      if (req.method === 'POST' && req.url === '/api/auth/operator-sign-in')
        return send(200, { signInId: 'sign-in-1', userCode: 'WDJB-MJHT', verificationUri: `${studio.url}/#/terminal-sign-in`,
          verificationUriComplete: `${studio.url}/#/terminal-sign-in?code=WDJBMJHT`, pollHandle: fakeToken('osp'), interval: 1,
          expiresAt: new Date(Date.now() + 600_000).toISOString(), keyThumbprint: keyThumbprint(parsed.publicKey) });
      if (req.method === 'POST' && req.url === '/api/auth/operator-sign-in/poll')
        return send(200, polls.shift() ?? { status: 'expired', interval: 1 });
      if (req.method === 'GET' && req.url.startsWith('/api/operator/decisions'))
        return send(200, { events: [], cursor: '0' });
      if (req.method === 'GET' && req.url === '/api/watch-paths') return send(200, [{ name: 'Demo App', path: '/demo/app' }]);
      return send(404, { error: 'not-found' });
    });
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  servers.push(server);
  const studio = { url: `http://127.0.0.1:${server.address().port}`, requests };
  return studio;
}

function makeHome() {
  const home = fs.mkdtempSync(path.join(os.tmpdir(), 'studio-op-login-'));
  homes.push(home);
  fs.mkdirSync(path.join(home, '.config', 'agent-studio'), { recursive: true, mode: 0o700 });
  return home;
}

function run(args, home, url) {
  const env = { PATH: process.env.PATH, HOME: home, USERPROFILE: home, STUDIO_OP_URL: url };
  if (process.env.SystemRoot) env.SystemRoot = process.env.SystemRoot;
  const child = spawn(process.execPath, [bin, ...args], { env, windowsHide: true });
  let stdout = '';
  let stderr = '';
  child.stdout.on('data', chunk => { stdout += chunk; });
  child.stderr.on('data', chunk => { stderr += chunk; });
  return new Promise(resolve => child.on('close', code => {
    let json = null;
    try { json = JSON.parse(stdout); } catch { json = null; }
    resolve({ code, stdout, stderr, json });
  }));
}

test('login writes the confirmed session to operator-session with mode 600 and never prints it', async () => {
  const session = fakeToken('oas');
  const studio = await signInStudio([
    { status: 'pending', interval: 1 },
    { status: 'slow-down', interval: 1 },
    { status: 'session', interval: 1, token: session, operatorAgentId: 'opa_1', sessionId: session.split('.')[1],
      actor: 'operator-agent:opa_1/session:x', delegatedBy: 'human:owner', expiresAt: new Date(Date.now() + 3600_000).toISOString(),
      classes: ['project.testing'], keyThumbprint: 'thumb' },
  ]);
  const home = makeHome();
  const result = await run(['login', '--agent', 'ops console', '--label', 'agent@workstation', '--class', 'project.testing', '--json'],
    home, studio.url);
  assert.equal(result.code, 0, result.stderr);
  assert.equal(result.json.ok, true);
  assert.equal(result.json.actor, 'operator-agent:opa_1/session:x');
  assert.ok(!result.stdout.includes(session) && !result.stderr.includes(session));
  // The code, the link and a QR code go to stderr, so --json keeps stdout parseable.
  assert.match(result.stderr, /Code: {3}WDJB-MJHT/);
  assert.match(result.stderr, /#\/terminal-sign-in\?code=WDJBMJHT/);
  assert.match(result.stderr, /[▀▄█]{10,}/);

  const folder = path.join(home, '.config', 'agent-studio');
  const file = path.join(folder, 'operator-session');
  assert.equal(fs.readFileSync(file, 'utf8').trim(), session);
  if (posix) assert.equal(fs.statSync(file).mode & 0o777, 0o600);
  assert.equal(JSON.parse(fs.readFileSync(path.join(folder, 'studio-op.json'), 'utf8')).credential, 'operator-session');

  const [start] = studio.requests;
  assert.equal(start.authorization, null);
  assert.deepEqual(Object.keys(start.body.publicKey).sort(), ['crv', 'kty', 'x', 'y']);
  assert.equal(start.body.publicKey.crv, 'P-256');
  assert.deepEqual([start.body.deviceLabel, start.body.operatorAgent, start.body.classes], ['agent@workstation', 'ops console', ['project.testing']]);
  const keyFile = path.join(folder, 'operator-device-key.json');
  assert.equal(fs.existsSync(keyFile), false, 'the private key is never persisted');
  assert.equal(studio.requests.filter(request => request.path === '/api/auth/operator-sign-in/poll').length, 3);

  // auth status reads the session file the login selected.
  const status = await run(['auth', 'status', '--json'], home, studio.url);
  assert.equal(status.code, 0, status.stderr);
  assert.equal(status.json.credential.name, 'operator-session');
  assert.equal(status.json.credential.prefix, 'oas');
  assert.equal(status.json.server.authenticated, true);
  assert.ok(studio.requests.find(request => request.authorization)?.proof, 'CLI commands carry DPoP');
  assert.ok(studio.requests.filter(request => request.path === '/api/auth/operator-sign-in/poll').every(request => request.proof));
  assert.ok(!status.stdout.includes(session));

  // A second login gets a fresh memory key; denial keeps the earlier session and signer.
  const again = await signInStudio([{ status: 'denied', interval: 1, message: 'The Owner denied this sign-in.' }]);
  const denied = await run(['login', '--no-qr', '--json'], home, again.url);
  assert.equal(denied.code, 13);
  assert.equal(denied.json.error, 'sign-in-denied');
  assert.notDeepEqual(again.requests[0].body.publicKey, start.body.publicKey);
  assert.equal(fs.readFileSync(file, 'utf8').trim(), session, 'a denied login keeps the earlier session file');
});

test('an expired code ends login with its own exit code', async () => {
  const studio = await signInStudio([{ status: 'expired', interval: 1 }]);
  const result = await run(['login', '--no-qr'], makeHome(), studio.url);
  assert.equal(result.code, 14);
  assert.match(result.stderr, /sign-in-expired/);
});

test('login refuses plain HTTP to another host and retires the ES-3 disk key', async () => {
  const home = makeHome();
  const remote = await run(['login', '--no-qr'], home, 'http://studio.example.test');
  assert.equal(remote.code, 3);
  assert.match(remote.stderr, /token-requires-https/);
  if (!posix) return;
  const keyFile = path.join(home, '.config', 'agent-studio', 'operator-device-key.json');
  fs.writeFileSync(keyFile, '{}', { mode: 0o644 });
  fs.chmodSync(keyFile, 0o644);
  const studio = await signInStudio([{ status: 'denied', interval: 1 }]);
  const unsafe = await run(['login', '--no-qr'], home, studio.url);
  assert.equal(unsafe.code, 13);
  assert.equal(fs.existsSync(keyFile), false);
});

test('login submits hardware certification before showing the code and records its binding kind', async () => {
  const { login } = await import('../src/login.mjs');
  const { MemoryKeyProvider } = await import('../src/device-key.mjs');
  const configDir = path.join(makeHome(), '.config', 'agent-studio');
  const provider = new MemoryKeyProvider(); provider.bindingKind = 'tpm';
  const notes = [];
  let certified = false;
  const token = fakeToken('oas');
  const pollHandle = fakeToken('osp');
  const ctx = { configDir, config: {}, output: { note(text) { notes.push(text); }, protect() {} },
    client: { base: new URL('https://studio.test/'), url: p => new URL(p, 'https://studio.test/'),
      async request() { return { signInId: 'signin-test', pollHandle, userCode: 'WDJB-MJHT', interval: 5,
        expiresAt: new Date(Date.now() + 600_000).toISOString(), verificationUriComplete: 'https://studio.test/#/terminal-sign-in' }; },
      async send(method, url, { body, headers }) {
        assert.ok(headers.DPoP);
        if (body.certification) {
          assert.equal(notes.some(note => note.includes('Code:')), false, 'Owner code is not shown before certification');
          assert.deepEqual(body.certification.publicKey, provider.publicJwk);
          certified = true;
          return { status: 200, body: { status: 'pending', bindingKind: 'tpm' } };
        }
        assert.equal(certified, true);
        return { status: 200, body: { status: 'session', token, bindingKind: 'tpm', expiresAt: new Date(Date.now() + 3600_000).toISOString() } };
      } } };
  try {
    const result = await login(ctx, { noQr: true }, { wait: async () => {}, deviceKeyFactory: () => ({ provider, note: 'Device binding: tpm' }) });
    assert.equal(result.data.bindingKind, 'tpm');
    assert.ok(notes.some(note => note.includes('Code:')));
  } finally { await stopAgent(configDir); }
});
