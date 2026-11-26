import crypto from 'node:crypto';
import fs from 'node:fs';
import net from 'node:net';
import os from 'node:os';
import path from 'node:path';
import { fork, spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import readline from 'node:readline';

const moduleFile = fileURLToPath(import.meta.url);
export const AGENT_FILE = 'operator-session-agent.json';
export const digest = value => crypto.createHash('sha256').update(value).digest('base64url');
export const keyThumbprint = jwk => digest(JSON.stringify({ crv: jwk.crv, kty: jwk.kty, x: jwk.x, y: jwk.y }));

export function proofFor(key, publicJwk, method, address, token) {
  const uri = new URL(address);
  uri.search = ''; uri.hash = '';
  const header = Buffer.from(JSON.stringify({ typ: 'dpop+jwt', alg: 'ES256', jwk: publicJwk })).toString('base64url');
  const payload = Buffer.from(JSON.stringify({ htm: method, htu: uri.href, iat: Math.floor(Date.now() / 1000),
    jti: crypto.randomUUID(), ath: digest(token) })).toString('base64url');
  const input = `${header}.${payload}`;
  return `${input}.${crypto.sign('sha256', Buffer.from(input), { key, dsaEncoding: 'ieee-p1363' }).toString('base64url')}`;
}

/** A bounded one-message protocol. Only public keys and signed proofs leave the resident process. */
export function agentCall(endpoint, request) {
  return new Promise((resolve, reject) => {
    const socket = net.createConnection(endpoint);
    const timer = setTimeout(() => socket.destroy(new Error('Signer timed out.')), 3000);
    let text = '';
    socket.on('connect', () => socket.write(`${JSON.stringify(request)}\n`));
    socket.on('data', chunk => {
      text += chunk;
      if (text.length > 16_384) return socket.destroy(new Error('Signer response too large.'));
      if (!text.includes('\n')) return;
      try {
        const answer = JSON.parse(text.split('\n')[0]);
        if (answer.error) reject(new Error(answer.error)); else resolve(answer);
      } catch (error) { reject(error); }
      socket.end();
    });
    socket.on('error', reject);
    socket.on('close', () => { clearTimeout(timer); if (!text.includes('\n')) reject(new Error('Signer closed.')); });
  });
}

export function startAgent(configDir) {
  return new Promise((resolve, reject) => {
    const child = fork(moduleFile, [configDir], { detached: true, stdio: ['ignore', 'ignore', 'ignore', 'ipc'], windowsHide: true });
    const timer = setTimeout(() => { child.kill(); reject(new Error('Signer did not start.')); }, 10_000);
    child.once('error', error => { clearTimeout(timer); reject(error); });
    child.once('exit', code => { clearTimeout(timer); reject(new Error(`Signer exited (${code}).`)); });
    child.once('message', answer => {
      clearTimeout(timer);
      if (answer.error) { child.kill(); reject(new Error(answer.error)); return; }
      child.disconnect(); child.unref();
      resolve(answer);
    });
  });
}

/** Missing agent metadata is intentional for unbound Settings sessions and copied session files. */
export async function signFromAgent(configDir, token, method, url) {
  let metadata;
  try { metadata = JSON.parse(fs.readFileSync(path.join(configDir, AGENT_FILE), 'utf8')); }
  catch { return null; }
  if (metadata.tokenHash !== digest(token)) return null;
  try { return (await agentCall(metadata.endpoint, { operation: 'sign', tokenHash: digest(token), method, url: String(url) })).proof; }
  catch { return null; } // The server supplies the typed dpop-required refusal.
}

export async function stopAgent(configDir) {
  try {
    const metadata = JSON.parse(fs.readFileSync(path.join(configDir, AGENT_FILE), 'utf8'));
    await agentCall(metadata.endpoint, { operation: 'stop' });
  } catch { /* already ended */ }
}

export async function serveAgent(configDir, ready = () => {}) {
  // The private KeyObject is never exported, serialized or persisted.
  let { privateKey, publicKey } = crypto.generateKeyPairSync('ec', { namedCurve: 'P-256' });
  const publicJwk = publicKey.export({ format: 'jwk' });
  const thumbprint = keyThumbprint(publicJwk);
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'studio-op-agent-'));
  if (process.platform !== 'win32') fs.chmodSync(directory, 0o700);
  const endpoint = process.platform === 'win32' ? `\\\\.\\pipe\\studio-op-${crypto.randomUUID()}` : path.join(directory, 'sign.sock');
  let session = null;
  let ended = false;
  let server;
  let bridge;
  let checking = false;
  const close = () => {
    if (ended) return;
    ended = true;
    privateKey = null;
    clearInterval(monitor); clearTimeout(pendingEnd);
    server?.close(); bridge?.kill();
    fs.rmSync(directory, { recursive: true, force: true });
  };
  const handle = message => {
    if (ended) throw new Error('Session ended.');
    if (message.operation === 'stop') { setImmediate(close); return { stopped: true }; }
    if (message.operation === 'attach') {
      if (session) throw new Error('Session already attached.');
      const expires = Date.parse(message.expiresAt);
      const origin = new URL(message.baseUrl);
      if (!Number.isFinite(expires) || expires <= Date.now() || !message.token?.startsWith('oas.')) throw new Error('Invalid session.');
      session = { token: message.token, expires, baseUrl: origin.href };
      clearTimeout(pendingEnd);
      return { attached: true };
    }
    if (message.operation === 'poll') {
      if (session) throw new Error('Sign-in finished.');
      const uri = new URL(message.url);
      if (!uri.pathname.endsWith('/api/auth/operator-sign-in/poll')) throw new Error('Only sign-in polling is allowed.');
      return { proof: proofFor(privateKey, publicJwk, 'POST', uri.href, message.pollHandle) };
    }
    if (message.operation !== 'sign' || !session || session.expires <= Date.now()
      || message.tokenHash !== digest(session.token)) throw new Error('No active session.');
    const uri = new URL(message.url);
    if (uri.origin !== new URL(session.baseUrl).origin) throw new Error('Different Studio origin.');
    if (!/^[A-Z]+$/.test(message.method)) throw new Error('Invalid method.');
    return { proof: proofFor(privateKey, publicJwk, message.method, uri.href, session.token) };
  };
  const answer = line => {
    try { return JSON.stringify(handle(JSON.parse(line))); }
    catch { return JSON.stringify({ error: 'Signer refused the request.' }); }
  };
  // An abandoned or denied login leaves no resident key behind.
  const pendingEnd = setTimeout(close, 11 * 60_000);
  const monitor = setInterval(async () => {
    if (!session || checking) return;
    checking = true;
    try {
      const file = path.join(configDir, 'operator-session');
      if (session.expires <= Date.now() || !fs.existsSync(file) || fs.readFileSync(file, 'utf8').trim() !== session.token) return close();
      const root = session.baseUrl.endsWith('/') ? session.baseUrl : session.baseUrl + '/';
      const url = new URL('api/operator/decisions?limit=1', root);
      const response = await fetch(url, { headers: { Authorization: `Bearer ${session.token}`,
        DPoP: proofFor(privateKey, publicJwk, 'GET', url.href, session.token) }, redirect: 'manual', signal: AbortSignal.timeout(5000) });
      await response.body?.cancel();
      if (response.status === 401) close();
    } catch { /* transport loss grants no authority; the server still enforces expiry and revocation */ }
    finally { checking = false; }
  }, 5000);
  process.once('SIGTERM', close); process.once('SIGINT', close);
  if (process.platform === 'win32') {
    // Windows PowerShell provides an explicit user-only pipe ACL; Node's default pipe ACL is insufficient.
    bridge = spawn('powershell.exe', ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File',
      fileURLToPath(new URL('./agent-pipe.ps1', import.meta.url)), endpoint.split('\\').at(-1)],
    { stdio: ['pipe', 'pipe', 'ignore'], windowsHide: true });
    const lines = readline.createInterface({ input: bridge.stdout });
    await new Promise((resolve, reject) => {
      bridge.once('error', reject); bridge.once('exit', () => { close(); reject(new Error('Pipe bridge exited.')); });
      lines.on('line', line => {
        if (line === 'READY') resolve();
        else bridge.stdin.write(answer(line) + '\n');
      });
    });
  } else {
    server = net.createServer(socket => {
      socket.setTimeout(3000, () => socket.destroy());
      let buffer = '';
      socket.on('error', () => {});
      socket.on('data', chunk => {
        buffer += chunk;
        if (buffer.length > 8192) return socket.destroy();
        if (buffer.includes('\n')) socket.end(answer(buffer.split('\n')[0]) + '\n');
      });
    });
    await new Promise((resolve, reject) => { server.once('error', reject); server.listen(endpoint, resolve); });
    fs.chmodSync(endpoint, 0o600);
  }
  ready({ endpoint, publicJwk, thumbprint });
  return { close, endpoint, publicJwk, thumbprint };
}

if (process.argv[1] && path.resolve(process.argv[1]) === moduleFile) {
  serveAgent(process.argv[2], info => process.send?.(info)).catch(() => { process.send?.({ error: 'Signer failed to start.' }); process.exit(1); });
}
