// Run: node --test .agents/skills/task-api/scripts/access.test.mjs
//
// OP-1 (Dossier operator-agent-level): the task-api scripts read the API
// token only from ~/.config/agent-studio/operator-token with owner-only
// permissions, refuse the retired environment variable and any token on the
// command line, and never print the token.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import fs from 'node:fs';
import http from 'node:http';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';
import { taskApiRequest } from './access.js';

const run = promisify(execFile);
const scripts = path.dirname(fileURLToPath(import.meta.url));
const posix = process.platform !== 'win32';
const RETIRED = ['AGENT', 'STUDIO', 'API', 'TOKEN'].join('_');

/** A token-shaped value assembled at runtime, never a literal. */
function fakeToken() {
  return ['api', 'f'.repeat(32), Buffer.from(String(Math.random())).toString('base64url').padEnd(43, 'x')].join('.');
}

function writeTokenFile(home, token, mode = 0o600) {
  const dir = path.join(home, '.config', 'agent-studio');
  fs.mkdirSync(dir, { recursive: true });
  const file = path.join(dir, 'operator-token');
  fs.writeFileSync(file, token + '\n', { mode });
  fs.chmodSync(file, mode);
  return file;
}

/**
 * A stand-in Studio that echoes the Authorization header it received in
 * every body, so a script that prints a response would leak the token.
 */
async function startServer(laneRoot) {
  const seen = [];
  const server = http.createServer((req, res) => {
    let body = '';
    req.on('data', chunk => body += chunk);
    req.on('end', () => {
      const authorization = req.headers.authorization ?? null;
      seen.push({ method: req.method, path: req.url, authorization, clientId: req.headers['x-client-id'] });
      res.writeHead(200, { 'Content-Type': 'application/json' });
      if (req.url === '/api/watch-paths')
        res.end(JSON.stringify([{ name: 'Agent Task Processor', path: laneRoot }]));
      else if (req.url === '/api/tasks/grouped')
        res.end(JSON.stringify({ ready: [{ id: 'DEMO-1', title: `echo ${authorization}`, projectName: 'Demo' }] }));
      else
        res.end(JSON.stringify({ echo: authorization }));
    });
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  return {
    seen,
    url: `http://127.0.0.1:${server.address().port}`,
    close: () => new Promise((resolve, reject) => server.close(error => error ? reject(error) : resolve())),
  };
}

function tempHome() {
  return fs.mkdtempSync(path.join(os.tmpdir(), 'task-api-home-'));
}

function laneRoot() {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'task-api-lanes-'));
  fs.mkdirSync(path.join(root, '5-human-review', 'demo-card'), { recursive: true });
  return root;
}

/** Every task-api script, with arguments that reach at least one request. */
const SCRIPT_RUNS = [
  ['find-tasks.js'],
  ['create-task.js'],
  ['move-state.js', 'DEMO-1', '6-completed'],
  ['move-to-top.js', 'DEMO-1'],
  ['triage-lane.js'],
];

function scriptEnv(home, url, extra = {}) {
  const env = { ...process.env, HOME: home, USERPROFILE: home, TASKBOARD_URL: url, ...extra };
  delete env[RETIRED];
  return { ...env, ...extra };
}

async function runScript(args, env) {
  try {
    const { stdout, stderr } = await run(process.execPath, [path.join(scripts, args[0]), ...args.slice(1)], { env, timeout: 20_000 });
    return { code: 0, stdout, stderr };
  } catch (error) {
    return { code: error.code ?? 1, stdout: error.stdout ?? '', stderr: error.stderr ?? '' };
  }
}

async function withEnv(values, action) {
  const prior = Object.fromEntries(Object.keys(values).map(key => [key, process.env[key]]));
  for (const [key, value] of Object.entries(values)) {
    if (value === undefined) delete process.env[key];
    else process.env[key] = value;
  }
  try { return await action(); }
  finally {
    for (const [key, value] of Object.entries(prior)) {
      if (value === undefined) delete process.env[key];
      else process.env[key] = value;
    }
  }
}

function send() {
  return new Promise((resolve, reject) => {
    const req = taskApiRequest({ hostname: '127.0.0.1', port: 1, path: '/api/tasks',
      method: 'GET', headers: { 'X-Client-Id': 'local-default' } }, res => {
      res.resume(); res.on('end', resolve);
    });
    req.on('error', reject); req.end();
  });
}

test('a token file with owner-only permissions authenticates every request', async () => {
  const home = tempHome();
  const token = fakeToken();
  const server = await startServer(laneRoot());
  try {
    await withEnv({ HOME: home, USERPROFILE: home, TASKBOARD_URL: server.url, [RETIRED]: undefined }, async () => {
      await send();
      writeTokenFile(home, token);
      await send();
    });
    assert.deepEqual(server.seen.map(row => ({ authorization: row.authorization, clientId: row.clientId })), [
      { authorization: null, clientId: 'local-default' },
      { authorization: `Bearer ${token}`, clientId: 'local-default' },
    ]);
  } finally {
    await server.close();
  }
});

for (const [label, mode] of [['group', 0o640], ['others', 0o604]]) {
  test(`a token file readable by ${label} is refused with a named error`, { skip: !posix }, async () => {
    const home = tempHome();
    const server = await startServer(laneRoot());
    try {
      writeTokenFile(home, fakeToken(), mode);
      await withEnv({ HOME: home, USERPROFILE: home, TASKBOARD_URL: server.url, [RETIRED]: undefined }, async () => {
        await assert.rejects(send(), error => error.code === 'token-file-permissions');
      });
      assert.equal(server.seen.length, 0);
    } finally {
      await server.close();
    }
  });
}

test('the retired environment variable is refused and never authenticates', async () => {
  const home = tempHome();
  const token = fakeToken();
  const server = await startServer(laneRoot());
  try {
    await withEnv({ HOME: home, USERPROFILE: home, TASKBOARD_URL: server.url, [RETIRED]: token }, async () => {
      await assert.rejects(send(), error => error.code === 'token-environment-refused' && !error.message.includes(token));
    });
    for (const args of SCRIPT_RUNS) {
      const result = await runScript(args, scriptEnv(home, server.url, { [RETIRED]: token }));
      assert.notEqual(result.code, 0, `${args[0]} must fail`);
      assert.match(result.stderr, /token-environment-refused/, `${args[0]} names the refusal`);
      assert.ok(!(result.stdout + result.stderr).includes(token), `${args[0]} printed the token`);
    }
    assert.equal(server.seen.length, 0, 'no request may leave with the variable set');
  } finally {
    await server.close();
  }
});

test('an HTTPS origin without a token file fails with a named error', async () => {
  const home = tempHome();
  await withEnv({ HOME: home, USERPROFILE: home, TASKBOARD_URL: 'https://studio.example.invalid', [RETIRED]: undefined }, async () => {
    assert.throws(() => taskApiRequest({ path: '/api/tasks', method: 'GET', headers: {} }, () => {}),
      error => error.code === 'token-file-missing');
  });
});

test('a token on the command line is refused', async () => {
  const home = tempHome();
  const token = fakeToken();
  const server = await startServer(laneRoot());
  try {
    writeTokenFile(home, token);
    const result = await runScript(['find-tasks.js', '--grep', token], scriptEnv(home, server.url));
    assert.notEqual(result.code, 0);
    assert.match(result.stderr, /token-argument-refused/);
    assert.ok(!(result.stdout + result.stderr).includes(token));
    assert.equal(server.seen.length, 0);
  } finally {
    await server.close();
  }
});

test('no script reads a token from the environment or accepts a token option', () => {
  for (const file of fs.readdirSync(scripts).filter(name => name.endsWith('.js'))) {
    const source = fs.readFileSync(path.join(scripts, file), 'utf8');
    assert.doesNotMatch(source, /process\.env\.[A-Z_]*TOKEN/, `${file} reads a token variable`);
    assert.doesNotMatch(source, /['"]--(?:api-)?token['"]/, `${file} accepts a token option`);
  }
});

test('no script output contains the token, even when the server echoes it', async () => {
  const home = tempHome();
  const token = fakeToken();
  const server = await startServer(laneRoot());
  try {
    writeTokenFile(home, token);
    for (const args of SCRIPT_RUNS) {
      const result = await runScript(args, scriptEnv(home, server.url));
      assert.equal(result.code, 0, `${args[0]} failed: ${result.stderr}`);
      assert.ok(!(result.stdout + result.stderr).includes(token), `${args[0]} printed the token`);
    }
    assert.ok(server.seen.length >= SCRIPT_RUNS.length);
    assert.ok(server.seen.every(row => row.authorization === `Bearer ${token}`), 'every request authenticates from the file');
  } finally {
    await server.close();
  }
});
