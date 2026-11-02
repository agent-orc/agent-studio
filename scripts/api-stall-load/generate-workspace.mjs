#!/usr/bin/env node
// Synthetic workspace at the size of the 1 November 2026 stall finding (AGT-3369).
//
// Writes a TaskRepository with one project: 246 cards on the board, the rest
// of the cards in the archive, and enough per-card evidence (prompt, task.json,
// pipeline record, run logs, results) that the workspace git repository tracks
// about 85,000 files. The project's code repository is a clone of the given
// source repository with a `develop` branch, so git projections have real
// history to read.
//
// The data is invented and deterministic; re-running with --force rebuilds it.
//
// Usage:
//   node scripts/api-stall-load/generate-workspace.mjs --root <dir> --repo <git repo to clone> [--force]
//        [--board 246] [--cards 3400] [--files-per-card 25]

import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';

const LANES = [
  ['0-backlog', 70],
  ['1-preparation', 12],
  ['2-ready', 40],
  ['3-progress', 4],
  ['4-auto-review', 20],
  ['5-human-review', 30],
  ['6-completed', 70],
];
const PROJECT = 'Load Project';
const SLUG = 'load-project';
const KEY_PREFIX = 'LOAD';
const BASE = Date.parse('2026-06-01T08:00:00.000Z');

function parseArgs(argv) {
  const args = { board: 246, cards: 3400, filesPerCard: 25, force: false };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--root') args.root = resolve(argv[++i]);
    else if (a === '--repo') args.repo = resolve(argv[++i]);
    else if (a === '--board') args.board = Number(argv[++i]);
    else if (a === '--cards') args.cards = Number(argv[++i]);
    else if (a === '--files-per-card') args.filesPerCard = Number(argv[++i]);
    else if (a === '--force') args.force = true;
  }
  if (!args.root || !args.repo) {
    console.error('usage: generate-workspace.mjs --root <dir> --repo <git repo> [--force]');
    process.exit(2);
  }
  return args;
}

const iso = (minutes) => new Date(BASE + minutes * 60_000).toISOString();

function write(path, body) {
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, body);
}

function git(cwd, ...args) {
  return execFileSync('git', args, { cwd, stdio: ['ignore', 'pipe', 'inherit'], maxBuffer: 1 << 28, windowsHide: true }).toString();
}

function laneFor(index, board) {
  let seen = 0;
  const total = LANES.reduce((sum, [, n]) => sum + n, 0);
  for (const [lane, share] of LANES) {
    seen += Math.round((share / total) * board);
    if (index < seen) return lane;
  }
  return index < board ? '0-backlog' : '7-archive';
}

function logBody(key, run, lines) {
  const out = [];
  for (let i = 0; i < lines; i++) {
    out.push(JSON.stringify({
      ts: iso(run * 7 + i),
      kind: i % 5 === 0 ? 'tool_call' : 'assistant_text',
      run,
      text: `${key} run ${run} line ${i}: synthetic agent output for the stall load test, padded to a realistic line length.`,
    }));
  }
  return out.join('\n') + '\n';
}

function writeCard(projectDir, n, state, filesPerCard) {
  const key = `${KEY_PREFIX}-${n}`;
  const dir = join(projectDir, 'tasks', String(Math.floor(n / 1000)).padStart(3, '0'), key);
  const created = iso(n * 11);
  const done = state === '6-completed' || state === '7-archive';
  write(join(dir, 'task.json'), JSON.stringify({
    id: `load-card-${n}`,
    title: `Load card ${n}: synthetic work item`,
    createdAt: created,
    enteredLaneAt: iso(n * 11 + 90),
    state,
    order: n * 10,
    agent: 'claude',
    ownerClientId: 'local-default',
    model: 'claude-opus-4-8',
    thinkingLevel: 'medium',
    cliType: 'claude',
    kind: 'task',
    mode: 'coding',
    allowWebAccess: false,
    taskType: n % 3 === 0 ? 'bug' : 'feature',
    key,
    noBranchExpected: done,
    tags: ['load', `area-${n % 7}`],
    sessionName: `load-session-${n}`,
    lastProgressAt: iso(n * 11 + 80),
  }, null, 2) + '\n');
  write(join(dir, 'prompt.md'), `# Load card ${n}\n\nSynthetic card for the API stall load test (AGT-3369).\n`);
  write(join(dir, 'pipeline-execution.json'), JSON.stringify({
    pipelineId: 'standard-task-pipeline',
    pipelineVersion: 1,
    jobId: `load-card-${n}`,
    project: PROJECT,
    startedAt: iso(n * 11 + 10),
    steps: [
      { stepId: 'core-agent-run', kind: 1, model: 'claude-opus-4-8', status: 2, startedAt: iso(n * 11 + 10), completedAt: iso(n * 11 + 60), durationMs: 3_000_000, inputTokens: 40_000, outputTokens: 30_000 },
      { stepId: 'aspect-code-quality', kind: 2, model: 'claude-haiku-4-5', status: 2, startedAt: iso(n * 11 + 60), completedAt: iso(n * 11 + 62), durationMs: 120_000 },
    ],
  }, null, 2) + '\n');
  write(join(dir, 'logs', 'timeline.jsonl'), [
    { ts: created, kind: 'prompt_created', actor: 'human:local-default', summary: `Task created: Load card ${n}` },
    { ts: iso(n * 11 + 10), kind: 'agent_run_started', actor: 'system', summary: 'claude CLI start' },
    { ts: iso(n * 11 + 60), kind: 'agent_run_finished', actor: 'agent', summary: 'claude run finished' },
  ].map((e) => JSON.stringify(e)).join('\n') + '\n');
  write(join(dir, 'logs', 'session-events.jsonl'), JSON.stringify({ Ts: iso(n * 11 + 10), Kind: 'start', Cli: 'claude', CapturedSessionId: `load-session-${n}` }) + '\n');

  // Remaining files: run logs and result evidence, like a card with several attempts.
  let written = 5;
  for (let i = 0; written < filesPerCard; i++, written++) {
    if (i % 3 === 0) write(join(dir, 'logs', `run-${i}.jsonl`), logBody(key, i, 40));
    else if (i % 3 === 1) write(join(dir, 'results', `report-${i}.md`), `# Report ${i} for ${key}\n\n${'Synthetic result line.\n'.repeat(30)}`);
    else write(join(dir, 'attempts', String(i), 'status.md'), `attempt ${i} of ${key}: done\n`);
  }
}

function main() {
  const args = parseArgs(process.argv.slice(2));
  if (existsSync(args.root)) {
    if (!args.force) {
      console.error(`${args.root} exists; pass --force to rebuild`);
      process.exit(1);
    }
    rmSync(args.root, { recursive: true, force: true });
  }
  mkdirSync(args.root, { recursive: true });

  const projectDir = join(args.root, 'projects', SLUG);
  const byState = {};
  const byKey = {};
  for (let n = 1; n <= args.cards; n++) {
    const state = laneFor(n - 1, args.board);
    writeCard(projectDir, n, state, args.filesPerCard);
    const rel = `${String(Math.floor(n / 1000)).padStart(3, '0')}/${KEY_PREFIX}-${n}`;
    (byState[state] ??= []).push(rel);
    byKey[`${KEY_PREFIX}-${n}`] = rel;
  }
  write(join(projectDir, 'id', 'by-state.json'), JSON.stringify(byState, null, 2) + '\n');
  write(join(projectDir, 'id', 'by-key.json'), JSON.stringify(byKey, null, 2) + '\n');
  write(join(args.root, 'project-settings.json'), JSON.stringify({
    [PROJECT]: { AutoCommit: false, AutoPushStrategy: 'off', RunnerMode: 'manual', MaxParallelism: 1, IntegrationBranch: 'develop', IntegrationStrategy: 'direct-merge' },
  }, null, 2) + '\n');

  // Both remotes are local bare repositories: the API pushes evidence and
  // integration branches during the run, and those pushes must never leave
  // the scratch root.
  const remotes = join(args.root, '.remote');
  const repo = join(args.root, 'code');
  execFileSync('git', ['clone', '--quiet', '--bare', '--single-branch', '--branch', 'main', args.repo, join(remotes, 'code.git')], { stdio: 'inherit', windowsHide: true });
  execFileSync('git', ['clone', '--quiet', join(remotes, 'code.git'), repo], { stdio: 'inherit', windowsHide: true });
  git(repo, 'checkout', '--quiet', '-B', 'develop');
  git(repo, 'push', '--quiet', 'origin', 'develop');
  git(repo, 'config', 'user.name', 'Stall Load');
  git(repo, 'config', 'user.email', 'stall-load@example.invalid');

  git(args.root, 'init', '--quiet', '--initial-branch', 'main');
  git(args.root, 'config', 'user.name', 'Stall Load');
  git(args.root, 'config', 'user.email', 'stall-load@example.invalid');
  write(join(args.root, '.gitignore'), '/code/\n/.remote/\n/agent-worktrees/\n/home/\n/.metadata/\n/logs/\n/.runtime/\n/gitconfig\n');
  git(args.root, 'add', '-A');
  git(args.root, 'commit', '--quiet', '-m', 'Synthetic stall load workspace');
  execFileSync('git', ['init', '--quiet', '--bare', join(remotes, 'workspace.git')], { windowsHide: true });
  git(args.root, 'remote', 'add', 'origin', join(remotes, 'workspace.git'));
  git(args.root, 'push', '--quiet', '-u', 'origin', 'main');
  const tracked = git(args.root, 'ls-files').split('\n').filter(Boolean).length;
  const board = Object.entries(byState).filter(([s]) => s !== '7-archive').reduce((sum, [, v]) => sum + v.length, 0);
  console.log(JSON.stringify({ root: args.root, project: PROJECT, projectDir, codeRepository: repo, cards: args.cards, board, trackedFiles: tracked }));
}

main();
