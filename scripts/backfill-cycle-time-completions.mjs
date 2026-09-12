#!/usr/bin/env node
// One-time, re-runnable backfill of completion timestamps for terminal tasks
// that predate the lane_changed ledger (or were archived without a recorded
// 6-completed entry). Writes ONE auditable sidecar per project:
//
//   <project>/.metadata/cycle-time-backfill.json
//   { "version": 1, "generatedAt": ..., "entries": { "<taskKey>": {
//       "completedAt": ISO-UTC, "source": ..., "confidence": ..., "commit": ... } } }
//
// The backend cycle-time reader (backend/Features/Projects/CycleTime/) consults
// the sidecar only when the ledger has no completion; such rows carry a
// "backfilled:<source>" data gap and enter the lead-time rollup only. The
// ledger itself is never touched.
//
// Evidence, best first:
//   git-completed-move      workspace-repo commit that renamed the task folder
//                           into a completed lane (5-completed / 6-completed)   [high]
//   git-archive-move        commit that renamed it from a non-terminal lane
//                           into an archive lane (6-archive / 7-archive)        [medium]
//   task-entered-lane       task.json enteredLaneAt while the state is terminal [medium]
//   git-terminal-first-seen first commit that contains the task already inside
//                           a terminal lane folder; an upper bound (usually the
//                           initial snapshot)                                   [low]
//   status-mtime            last write of status.md; last resort                [low]
//
// Usage:
//   node scripts/backfill-cycle-time-completions.mjs --project <projectRoot> [--repo <gitRoot>] [--write] [--out <file>] [--verbose]
//
// Without --write the tool is a dry run: it prints the plan and touches nothing.
// The output is deterministic (sorted keys); when the computed entries equal the
// existing sidecar's, the file is left byte-identical (generatedAt kept), so a
// re-run is a no-op.

import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const MIN_PLAUSIBLE = Date.parse('2020-01-01T00:00:00Z');

export const SOURCES = Object.freeze({
  gitCompletedMove: 'git-completed-move',
  gitArchiveMove: 'git-archive-move',
  taskEnteredLane: 'task-entered-lane',
  gitTerminalFirstSeen: 'git-terminal-first-seen',
  statusMtime: 'status-mtime',
});

// ---- lane classification ----------------------------------------------------

const COMPLETED_LANES = new Set(['5-completed', '6-completed']);
const ARCHIVE_LANES = new Set(['6-archive', '7-archive']);
const TERMINAL_STATES = new Set([...COMPLETED_LANES, ...ARCHIVE_LANES]);

/** Lane folder of a repo-relative task-file path under the project, or null (tasks/ and jobs/ shards carry no lane). */
export function laneOf(relPath, projectPrefix) {
  if (!relPath || !relPath.startsWith(projectPrefix + '/')) return null;
  const first = relPath.slice(projectPrefix.length + 1).split('/')[0];
  if (first === 'tasks' || first === 'jobs') return null;
  return /^\d/.test(first) ? first : null;
}

const isCompletedLane = lane => lane !== null && COMPLETED_LANES.has(lane);
const isArchiveLane = lane => lane !== null && ARCHIVE_LANES.has(lane);
const isTerminalLane = lane => isCompletedLane(lane) || isArchiveLane(lane);

// ---- git history parsing ----------------------------------------------------

/**
 * Parses `git log --reverse --format=@%H|%cI --name-status` output into
 * chronological commits: { sha, date, changes: [{ action: 'A'|'M'|'D'|'R', from?, path }] }.
 */
export function parseNameStatusLog(text) {
  const commits = [];
  let current = null;
  for (const raw of text.split('\n')) {
    const line = raw.replace(/\r$/, '');
    if (line.startsWith('@')) {
      const [sha, date] = line.slice(1).split('|');
      current = { sha, date, changes: [] };
      commits.push(current);
      continue;
    }
    if (!current || !line.includes('\t')) continue;
    const parts = line.split('\t');
    const status = parts[0];
    if (status.startsWith('R') || status.startsWith('C')) {
      if (parts.length >= 3) current.changes.push({ action: 'R', from: parts[1], path: parts[2] });
    } else if (status === 'A' || status === 'M' || status === 'D') {
      current.changes.push({ action: status, path: parts[1] });
    }
  }
  return commits;
}

/**
 * Follows every task file identity through its renames and returns, keyed by
 * the FINAL (current) repo-relative path, the terminal-lane evidence:
 * { completedMove?, archiveMove?, firstTerminalSeen? } each { at, commit }.
 * completedMove / archiveMove take the LAST qualifying move (the reader's
 * completion is the last entry into the completed lane); firstTerminalSeen is
 * the first commit that already contains the file inside a terminal lane.
 */
export function buildPathEvidence(commits, projectPrefix) {
  const byPath = new Map(); // current path -> { events: [] }
  for (const commit of commits) {
    for (const change of commit.changes) {
      if (change.action === 'A') {
        let record = byPath.get(change.path);
        if (!record) { record = { events: [] }; byPath.set(change.path, record); }
        record.events.push({ action: 'A', path: change.path, date: commit.date, sha: commit.sha });
      } else if (change.action === 'R') {
        let record = byPath.get(change.from);
        if (!record) { record = { events: [] }; }
        byPath.delete(change.from);
        byPath.set(change.path, record);
        record.events.push({ action: 'R', from: change.from, path: change.path, date: commit.date, sha: commit.sha });
      } else if (change.action === 'D') {
        byPath.delete(change.path);
      }
    }
  }

  const evidence = new Map();
  for (const [finalPath, record] of byPath) {
    let completedMove = null;
    let archiveMove = null;
    let firstTerminalSeen = null;
    for (const event of record.events) {
      const lane = laneOf(event.path, projectPrefix);
      if (!isTerminalLane(lane)) continue;
      const fromLane = event.action === 'R' ? laneOf(event.from, projectPrefix) : null;
      const anchor = { at: event.date, commit: event.sha };
      if (event.action === 'R' && isCompletedLane(lane) && !isCompletedLane(fromLane)) {
        completedMove = anchor; // last entry into a completed lane
      } else if (event.action === 'R' && isArchiveLane(lane) && !isTerminalLane(fromLane)) {
        archiveMove = anchor; // last direct move into archive; completed->archive is not a completion anchor
      } else if (!firstTerminalSeen) {
        firstTerminalSeen = anchor; // already terminal when first committed: upper bound
      }
    }
    if (completedMove || archiveMove || firstTerminalSeen)
      evidence.set(finalPath, { completedMove, archiveMove, firstTerminalSeen });
  }
  return evidence;
}

// ---- evidence selection ------------------------------------------------------

function plausible(value, nowMs) {
  const ms = typeof value === 'number' ? value : Date.parse(value ?? '');
  return Number.isFinite(ms) && ms >= MIN_PLAUSIBLE && ms <= nowMs + 24 * 3600 * 1000 ? ms : null;
}

/**
 * Picks the best available completion evidence, best first (see header).
 * Returns { completedAt (ISO UTC), source, confidence, commit? } or null.
 * An implausible timestamp (before 2020, or in the future) falls through to
 * the next tier instead of poisoning the sidecar.
 */
export function chooseEvidence({ completedMove, archiveMove, enteredLaneAt, firstTerminalSeen, statusMtime }, nowMs = Date.now()) {
  const tiers = [
    [completedMove?.at, SOURCES.gitCompletedMove, 'high', completedMove?.commit],
    [archiveMove?.at, SOURCES.gitArchiveMove, 'medium', archiveMove?.commit],
    [enteredLaneAt, SOURCES.taskEnteredLane, 'medium', undefined],
    [firstTerminalSeen?.at, SOURCES.gitTerminalFirstSeen, 'low', firstTerminalSeen?.commit],
    [statusMtime, SOURCES.statusMtime, 'low', undefined],
  ];
  for (const [value, source, confidence, commit] of tiers) {
    const ms = plausible(value, nowMs);
    if (ms === null) continue;
    const entry = { completedAt: new Date(ms).toISOString(), source, confidence };
    if (commit) entry.commit = commit;
    return entry;
  }
  return null;
}

// ---- sidecar composition -----------------------------------------------------

export function entriesEqual(a, b) {
  const keysA = Object.keys(a ?? {}).sort();
  const keysB = Object.keys(b ?? {}).sort();
  if (keysA.length !== keysB.length) return false;
  return keysA.every((key, i) => {
    if (key !== keysB[i]) return false;
    const x = a[key];
    const y = b[key];
    return x?.completedAt === y?.completedAt && x?.source === y?.source
      && x?.confidence === y?.confidence && (x?.commit ?? null) === (y?.commit ?? null);
  });
}

/** Deterministic sidecar document: sorted keys, unchanged file keeps its generatedAt. */
export function buildSidecar(entries, previous, { project, now = new Date() } = {}) {
  const sorted = {};
  for (const key of Object.keys(entries).sort((l, r) => l.localeCompare(r, 'en', { numeric: true })))
    sorted[key] = entries[key];
  const unchanged = previous && entriesEqual(previous.entries, sorted);
  return {
    unchanged: Boolean(unchanged),
    doc: {
      version: 1,
      generatedAt: unchanged ? previous.generatedAt : now.toISOString(),
      tool: 'scripts/backfill-cycle-time-completions.mjs',
      project,
      entries: sorted,
    },
  };
}

// ---- store scanning ----------------------------------------------------------

/** True when logs/timeline.jsonl records a lane change into 6-completed (the reader's primary completion source). */
export function hasLedgerCompletion(timelineText) {
  if (!timelineText) return false;
  for (const line of timelineText.split('\n')) {
    if (!line.includes('lane_changed') || !line.includes('6-completed')) continue;
    try {
      const event = JSON.parse(line);
      if (event?.kind === 'lane_changed' && event?.details?.to === '6-completed') return true;
    } catch { /* tolerate torn lines */ }
  }
  return false;
}

function readJson(file) {
  try { return JSON.parse(fs.readFileSync(file, 'utf8')); } catch { return null; }
}

function listDirs(dir) {
  try {
    return fs.readdirSync(dir, { withFileTypes: true }).filter(e => e.isDirectory()).map(e => e.name);
  } catch { return []; }
}

/** Task folders of a project store: lane folders (<root>/<lane>/<slug>) and shards (<root>/tasks|jobs/<shard>/<key>). */
export function collectTaskFolders(projectRoot) {
  const folders = [];
  for (const top of listDirs(projectRoot)) {
    const topPath = path.join(projectRoot, top);
    if (top === 'tasks' || top === 'jobs') {
      for (const shard of listDirs(topPath))
        for (const name of listDirs(path.join(topPath, shard)))
          folders.push(path.join(topPath, shard, name));
    } else if (/^\d/.test(top)) {
      for (const name of listDirs(topPath)) folders.push(path.join(topPath, name));
    }
  }
  return folders;
}

function loadTask(folder) {
  const file = ['task.json', 'job.json'].map(n => path.join(folder, n)).find(f => fs.existsSync(f));
  if (!file) return null;
  const json = readJson(file);
  if (!json || typeof json !== 'object') return null;
  return {
    folder,
    file,
    key: typeof json.key === 'string' && json.key ? json.key : null,
    id: typeof json.id === 'string' ? json.id : null,
    state: typeof json.state === 'string' ? json.state : '',
    kind: typeof json.kind === 'string' ? json.kind : 'task',
    enteredLaneAt: typeof json.enteredLaneAt === 'string' ? json.enteredLaneAt : null,
  };
}

// ---- main --------------------------------------------------------------------

function option(args, name, fallback) {
  const index = args.indexOf(name);
  if (index < 0) return fallback;
  const value = args[index + 1];
  if (!value || value.startsWith('--')) throw new Error(`${name} requires a value`);
  return value;
}

export function parseArgs(args) {
  return {
    project: option(args, '--project', null),
    repo: option(args, '--repo', null),
    out: option(args, '--out', null),
    write: args.includes('--write'),
    verbose: args.includes('--verbose'),
  };
}

function findRepoRoot(start) {
  let current = path.resolve(start);
  for (;;) {
    if (fs.existsSync(path.join(current, '.git'))) return current;
    const parent = path.dirname(current);
    if (parent === current) throw new Error(`No .git found above ${start}; pass --repo`);
    current = parent;
  }
}

function gitEvidence(repoRoot, prefix, verbose, write) {
  const result = spawnSync('git', [
    '-C', repoRoot, 'log', '--reverse', '--date=iso-strict',
    '--format=@%H|%cI', '--name-status', '-M', '--',
    `:(glob)${prefix}/**/task.json`, `:(glob)${prefix}/**/job.json`,
  ], { encoding: 'utf8', maxBuffer: 1024 * 1024 * 512, windowsHide: true });
  if (result.status !== 0)
    throw new Error(`git log failed: ${result.stderr || result.status}`);
  const commits = parseNameStatusLog(result.stdout);
  if (verbose) write(`git history: ${commits.length} commits touch ${prefix} task files`);
  return buildPathEvidence(commits, prefix);
}

export async function run(options, dependencies = {}) {
  const write = dependencies.write ?? console.log;
  if (!options.project) throw new Error('--project <projectRoot> is required');
  const projectRoot = path.resolve(options.project);
  if (!fs.existsSync(projectRoot)) throw new Error(`Project root not found: ${projectRoot}`);
  const repoRoot = path.resolve(options.repo ?? findRepoRoot(projectRoot));
  const prefix = path.relative(repoRoot, projectRoot).split(path.sep).join('/');
  const outFile = path.resolve(options.out ?? path.join(projectRoot, '.metadata', 'cycle-time-backfill.json'));

  const evidenceByPath = gitEvidence(repoRoot, prefix, options.verbose, write);

  const stats = {
    folders: 0, unreadable: 0, epics: 0, nonTerminal: 0,
    ledgerCovered: 0, laneEntryCovered: 0, candidates: 0,
    bySource: {}, unresolved: [],
  };
  const entries = {};
  const now = Date.now();

  for (const folder of collectTaskFolders(projectRoot)) {
    stats.folders++;
    const task = loadTask(folder);
    if (!task) { stats.unreadable++; continue; }
    if (task.kind.toLowerCase() === 'epic') { stats.epics++; continue; }
    if (!TERMINAL_STATES.has(task.state)) { stats.nonTerminal++; continue; }

    let timelineText = null;
    try { timelineText = fs.readFileSync(path.join(folder, 'logs', 'timeline.jsonl'), 'utf8'); } catch { /* no ledger */ }
    if (hasLedgerCompletion(timelineText)) { stats.ledgerCovered++; continue; }
    if (COMPLETED_LANES.has(task.state)) {
      // The reader's lane-entry fallback already dates tasks sitting in the
      // completed lane; the sidecar is only for the archived remainder.
      stats.laneEntryCovered++;
      continue;
    }

    stats.candidates++;
    const relFile = path.relative(repoRoot, task.file).split(path.sep).join('/');
    const git = evidenceByPath.get(relFile) ?? {};
    let statusMtime = null;
    try { statusMtime = fs.statSync(path.join(folder, 'status.md')).mtimeMs; } catch { /* no status.md */ }

    const chosen = chooseEvidence({
      completedMove: git.completedMove,
      archiveMove: git.archiveMove,
      enteredLaneAt: task.enteredLaneAt,
      firstTerminalSeen: git.firstTerminalSeen,
      statusMtime,
    }, now);

    const key = task.key ?? task.id;
    if (!key) { stats.unresolved.push(path.basename(folder)); continue; }
    if (!chosen) { stats.unresolved.push(key); continue; }
    if (entries[key]) { write(`WARN duplicate task key ${key} (${folder}); keeping first`); continue; }
    entries[key] = chosen;
    stats.bySource[chosen.source] = (stats.bySource[chosen.source] ?? 0) + 1;
    if (options.verbose) write(`  ${key} <- ${chosen.completedAt} (${chosen.source}, ${chosen.confidence})`);
  }

  const previous = fs.existsSync(outFile) ? readJson(outFile) : null;
  const { doc, unchanged } = buildSidecar(entries, previous, { project: prefix });

  write(`Project ${prefix} (repo ${repoRoot})`);
  write(`  task folders: ${stats.folders} (unreadable ${stats.unreadable}, epics ${stats.epics}, non-terminal ${stats.nonTerminal})`);
  write(`  already covered by the reader: ledger ${stats.ledgerCovered}, completed-lane entry ${stats.laneEntryCovered}`);
  write(`  backfill candidates: ${stats.candidates}, resolved: ${Object.keys(entries).length}, unresolved: ${stats.unresolved.length}`);
  for (const [source, count] of Object.entries(stats.bySource).sort((l, r) => r[1] - l[1]))
    write(`    ${source}: ${count}`);
  if (stats.unresolved.length)
    write(`  unresolved: ${stats.unresolved.slice(0, 20).join(', ')}${stats.unresolved.length > 20 ? ', ...' : ''}`);

  if (!options.write) {
    write(`Dry run. Pass --write to write ${outFile}`);
  } else if (unchanged) {
    write(`Unchanged: ${outFile} already carries these ${Object.keys(entries).length} entries`);
  } else {
    fs.mkdirSync(path.dirname(outFile), { recursive: true });
    fs.writeFileSync(outFile, JSON.stringify(doc, null, 2) + '\n');
    write(`Wrote ${outFile} (${Object.keys(entries).length} entries)`);
  }
  return { entries, stats, unchanged, outFile };
}

// pathToFileURL, not new URL(argv[1], 'file:'): a Windows drive letter would
// otherwise parse as the URL scheme and the guard would never match.
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  run(parseArgs(process.argv.slice(2))).catch(error => {
    console.error(error.message ?? error);
    process.exitCode = 1;
  });
}
