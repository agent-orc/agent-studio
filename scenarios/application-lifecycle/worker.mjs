// Use the same runner plane as ScenarioContext.RunLegacyPlaneCodingAttemptAsync,
// with a project-bound credential and a provider double confined to this fixture.
import { spawn, spawnSync } from 'node:child_process';
import { once } from 'node:events';
import { closeSync, mkdirSync, openSync, symlinkSync, writeFileSync } from 'node:fs';
import http from 'node:http';
import path from 'node:path';
import { setTimeout as sleep } from 'node:timers/promises';

export async function serveGit(workspace) {
  const server = http.createServer(async (req, res) => {
    const url = new URL(req.url, 'http://localhost');
    if (!/^\/counter-origin\.git\/(info\/refs|git-(upload|receive)-pack)$/.test(url.pathname)) {
      res.writeHead(404).end(); return;
    }
    const body = [];
    for await (const chunk of req) body.push(chunk);
    const response = spawnSync('git', ['http-backend'], { input: Buffer.concat(body), env: {
      PATH: process.env.PATH, HOME: path.join(workspace, 'home'), GIT_PROJECT_ROOT: workspace,
      GIT_HTTP_EXPORT_ALL: '1', PATH_INFO: url.pathname, REQUEST_METHOD: req.method,
      CONTENT_TYPE: req.headers['content-type'] ?? '', QUERY_STRING: url.search.slice(1),
    } });
    const bytes = response.stdout;
    const split = bytes?.indexOf('\r\n\r\n') ?? -1;
    if (split < 0) { res.writeHead(500).end(); return; }
    for (const header of bytes.subarray(0, split).toString().split('\r\n')) {
      const colon = header.indexOf(':');
      const key = header.slice(0, colon), value = header.slice(colon + 1).trim();
      if (key === 'Status') res.statusCode = Number(value.split(' ')[0]);
      else res.setHeader(key, value);
    }
    res.end(bytes.subarray(split + 4));
  });
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  return { url: 'http://127.0.0.1:' + server.address().port + '/counter-origin.git', server };
}

export async function startWorker({ workspace, studio, project, session, remote }, role) {
  const name = 'scenario-' + role;
  const enrollment = await session.post('/api/auth/runner-enrollments', { name, projects: [project.id] });
  const credential = await session.post('/api/auth/runner-enroll', { code: enrollment.enrollmentCode });
  const tokenFile = path.join(workspace, name + '.token');
  writeFileSync(tokenFile, credential.secret, { mode: 0o600 });
  const work = path.join(workspace, name + '-work');
  mkdirSync(work);
  const cliDir = path.join(workspace, name + '-cli');
  mkdirSync(cliDir);
  for (const name of ['codex', 'claude']) symlinkSync(path.resolve('scenarios/application-lifecycle/provider.sh'), path.join(cliDir, name));
  for (const [dir, file] of [['codex', 'auth.json'], ['claude', '.credentials.json']]) {
    const configDir = path.join(workspace, 'config', dir);
    mkdirSync(configDir, { recursive: true });
    writeFileSync(path.join(configDir, file), '{}', { mode: 0o600 });
  }
  const dll = path.resolve('runner/bin/Debug/net10.0/agent-host.dll');
  const log = path.join(workspace, name + '.log');
  const fd = openSync(log, 'w', 0o600);
  const worker = spawn('dotnet', [dll, '--poll', '--server', studio, '--auth-token-file', tokenFile,
    '--runner-id', credential.runnerId, '--runner-name', name, '--role', role, '--hostname', 'scenario-host',
    '--git-remote', remote, '--branch', 'develop', '--workdir', work, '--poll-seconds', '1',
    '--max-parallelism', '1', '--codex-cli', path.join(cliDir, 'codex'), '--claude-cli', path.join(cliDir, 'claude')], { cwd: workspace, stdio: ['ignore', fd, fd],
    env: { PATH: path.join(workspace, 'bin') + ':' + process.env.PATH, HOME: path.join(workspace, 'home'),
      XDG_CONFIG_HOME: path.join(workspace, 'config'), XDG_DATA_HOME: path.join(workspace, 'data'),
      XDG_CACHE_HOME: path.join(workspace, 'cache'), SSL_CERT_FILE: process.env.PW_E2E_TLS_CERT,
      GIT_CONFIG_GLOBAL: path.join(workspace, 'gitconfig'), SCENARIO_WORK_ROOT: work, SCENARIO_WORK_ROLE: role, RUNNER_REVIEW_WORKDIR: path.join(work, 'review'),
      CODEX_HOME: path.join(workspace, 'config/codex'), CLAUDE_CONFIG_DIR: path.join(workspace, 'config/claude'),
      AGENT_STUDIO_CLEAN_CONTEXT_ROOT: path.join(workspace, name + '-context'), RUNNER_CLI_TYPE: 'codex',
      PLAYWRIGHT_BROWSERS_PATH: process.env.PLAYWRIGHT_BROWSERS_PATH ?? path.join(process.env.HOME, '.cache/ms-playwright'),
      // The first-run fixture allows its single bounded claim on a shared host.
      RUNNER_CLAIM_MAX_LOAD_PER_CORE: '100',
      RUNNER_HOST_CODING_SLOTS: role === 'coding' ? '1' : '0',
      RUNNER_HOST_REVIEW_SLOTS: role === 'review' ? '1' : '0',
      DOTNET_PROCESSOR_COUNT: '2', RUNNER_HEARTBEAT_SECONDS: '3', RUNNER_RUN_TIMEOUT_SECONDS: '60' } });
  closeSync(fd);
  const exited = once(worker, 'exit');
  async function stop() {
    if (worker.exitCode === null && !worker.signalCode) {
      worker.kill();
      const timer = setTimeout(() => worker.kill('SIGKILL'), 5000);
      try { await exited; } finally { clearTimeout(timer); }
    }
  }
  try {
    if (role === 'coding') await session.put('/api/projects/Click%20Counter/execution-runner',
      { executionRunner: credential.runnerId, remoteExecutionEnabled: true, pickupMode: 'auto' });
    await sleep(1000);
    if (worker.exitCode !== null) throw Error('Fixture coding runner exited ' + worker.exitCode);
    return { stop, id: credential.runnerId };
  } catch (error) { await stop(); throw error; }
}
