#!/usr/bin/env node
// Load driver for the API stall measurement (AGT-3369).
//
// Runs against an API booted on a workspace from generate-workspace.mjs and
// keeps three kinds of pressure on it for the given duration:
//   - board readers: the grouped board, the flat task list and card details,
//     like several open Studio tabs;
//   - board writers: cards moved between backlog and ready, which invalidate
//     the task index and the board snapshot;
//   - agent churn: commits on agent worktrees of the code repository and
//     appended run logs on a few cards, which wake the git and file
//     watchers.
// A probe requests /healthz every 250 ms. Its latency is what a user sees as
// "backend offline": the summary counts probes slower than 5 seconds.
//
// Usage:
//   node scripts/api-stall-load/drive.mjs --base-url http://127.0.0.1:5199 --workspace <root>
//        --out <dir> [--duration-seconds 1800] [--readers 6]

import { execFileSync } from 'node:child_process';
import { appendFileSync, existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

const args = (() => {
  const a = { durationSeconds: 1800, readers: 6 };
  const argv = process.argv.slice(2);
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === '--base-url') a.baseUrl = argv[++i];
    else if (argv[i] === '--workspace') a.workspace = argv[++i];
    else if (argv[i] === '--out') a.out = argv[++i];
    else if (argv[i] === '--duration-seconds') a.durationSeconds = Number(argv[++i]);
    else if (argv[i] === '--readers') a.readers = Number(argv[++i]);
  }
  if (!a.baseUrl || !a.workspace || !a.out) {
    console.error('usage: drive.mjs --base-url <url> --workspace <root> --out <dir>');
    process.exit(2);
  }
  return a;
})();

const PROJECT = 'Load Project';
const STALL_MS = 5000;
const projectDir = join(args.workspace, 'projects', 'load-project');
const codeRepo = join(args.workspace, 'code');
mkdirSync(args.out, { recursive: true });
const probeCsv = join(args.out, 'probe.csv');
writeFileSync(probeCsv, 'utc,latency_ms,status\n');

const deadline = Date.now() + args.durationSeconds * 1000;
const running = () => Date.now() < deadline;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const stats = { requests: 0, errors: 0, byRoute: {} };

async function call(route, url, init = {}, timeoutMs = 120_000) {
  const started = performance.now();
  let status = 0;
  try {
    const res = await fetch(args.baseUrl + url, { ...init, signal: AbortSignal.timeout(timeoutMs) });
    status = res.status;
    await res.arrayBuffer();
  } catch {
    status = -1;
  }
  const ms = performance.now() - started;
  const r = (stats.byRoute[route] ??= { count: 0, errors: 0, totalMs: 0, maxMs: 0 });
  r.count++;
  r.totalMs += ms;
  r.maxMs = Math.max(r.maxMs, ms);
  stats.requests++;
  if (status < 200 || status >= 400) {
    r.errors++;
    stats.errors++;
  }
  return { status, ms };
}

const byState = JSON.parse(readFileSync(join(projectDir, 'id', 'by-state.json'), 'utf8'));
const keyOf = (rel) => rel.split('/')[1];
const backlog = byState['0-backlog'].map(keyOf);
// Cards whose run logs grow during the run, like cards with a live agent.
const churnCards = byState['6-completed'].slice(0, 4).map(keyOf);
const boardKeys = Object.entries(byState).filter(([s]) => s !== '7-archive').flatMap(([, v]) => v.map(keyOf));
const pick = (list) => list[Math.floor(Math.random() * list.length)];
// Routes take the job id (task.json `id`), which the generator derives from the card number.
const jobIdOf = (key) => `load-card-${key.split('-')[1]}`;
// The generated workspace registers one project, which the API numbers PROJ-001.
const PROJECT_ID = 'PROJ-001';
const CLIENT_ID = 'stall-load';

async function probe() {
  const probes = [];
  while (running()) {
    const { status, ms } = await call('healthz', '/healthz', {}, 600_000);
    probes.push(ms);
    appendFileSync(probeCsv, `${new Date().toISOString()},${ms.toFixed(1)},${status}\n`);
    await sleep(250);
  }
  return probes;
}

async function reader(id) {
  const routes = [
    ['grouped', () => '/api/tasks/grouped'],
    ['flat', () => '/api/tasks/'],
    ['detail', () => `/api/v1/projects/${PROJECT_ID}/tasks/${jobIdOf(pick(boardKeys))}`],
  ];
  let i = id;
  while (running()) {
    const [route, url] = routes[i++ % routes.length];
    await call(route, url());
    await sleep(500 + Math.random() * 500);
  }
}

async function writer() {
  while (running()) {
    const key = pick(backlog);
    const body = (targetState) => ({
      method: 'POST',
      headers: { 'content-type': 'application/json', 'x-client-id': CLIENT_ID },
      body: JSON.stringify({ targetState, runIntent: 'revoke' }),
    });
    await call('move', `/api/v1/projects/${PROJECT_ID}/tasks/${jobIdOf(key)}/move`, body('2-ready'));
    await sleep(1500);
    await call('move', `/api/v1/projects/${PROJECT_ID}/tasks/${jobIdOf(key)}/move`, body('0-backlog'));
    await sleep(1500);
  }
}

function git(cwd, ...gitArgs) {
  return execFileSync('git', gitArgs, { cwd, stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true }).toString();
}

async function agentChurn() {
  const worktrees = [];
  for (let i = 0; i < 4; i++) {
    const path = join(args.workspace, 'agent-worktrees', `agent-${i}`);
    if (!existsSync(path)) git(codeRepo, 'worktree', 'add', '--quiet', '-B', `runner/load/agent-${i}`, path, 'develop');
    worktrees.push(path);
  }
  let n = 0;
  while (running()) {
    const wt = worktrees[n % worktrees.length];
    writeFileSync(join(wt, 'stall-load.txt'), `change ${n}\n`);
    try {
      git(wt, 'add', 'stall-load.txt');
      git(wt, '-c', 'user.name=Stall Load', '-c', 'user.email=stall-load@example.invalid', 'commit', '--quiet', '-m', `load change ${n}`);
    } catch {
      stats.errors++;
    }
    for (const key of churnCards) {
      const bucket = String(Math.floor(Number(key.split('-')[1]) / 1000)).padStart(3, '0');
      appendFileSync(join(projectDir, 'tasks', bucket, key, 'logs', 'run-0.jsonl'),
        JSON.stringify({ ts: new Date().toISOString(), kind: 'assistant_text', text: `churn ${n}` }) + '\n');
    }
    n++;
    await sleep(5000);
  }
}

function percentile(sorted, p) {
  if (sorted.length === 0) return 0;
  return sorted[Math.min(sorted.length - 1, Math.floor((p / 100) * sorted.length))];
}

// Mutations need a registered client identity.
await call('register', '/api/clients/register', {
  method: 'POST',
  headers: { 'content-type': 'application/json' },
  body: JSON.stringify({ displayName: 'Stall Load' }),
});

const [probes] = await Promise.all([
  probe(),
  ...Array.from({ length: args.readers }, (_, i) => reader(i)),
  writer(),
  agentChurn(),
]);
const sorted = [...probes].sort((a, b) => a - b);
const summary = {
  durationSeconds: args.durationSeconds,
  probes: probes.length,
  probeP50Ms: Math.round(percentile(sorted, 50)),
  probeP99Ms: Math.round(percentile(sorted, 99)),
  probeMaxMs: Math.round(sorted.at(-1) ?? 0),
  probesOver5s: probes.filter((ms) => ms > STALL_MS).length,
  requests: stats.requests,
  errors: stats.errors,
  routes: Object.fromEntries(Object.entries(stats.byRoute).map(([k, v]) => [k, {
    count: v.count, errors: v.errors, meanMs: Math.round(v.totalMs / Math.max(1, v.count)), maxMs: Math.round(v.maxMs),
  }])),
};
writeFileSync(join(args.out, 'drive-summary.json'), JSON.stringify(summary, null, 2) + '\n');
console.log(JSON.stringify(summary));
