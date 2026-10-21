import assert from 'node:assert/strict';
import test from 'node:test';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';

import {
  buildPathEvidence,
  buildSidecar,
  chooseEvidence,
  entriesEqual,
  hasLedgerCompletion,
  laneOf,
  parseNameStatusLog,
  run,
  SOURCES,
} from './backfill-cycle-time-completions.mjs';

const P = 'projects/agent-taskboard';

test('backfill skips archived historical parent cards even when git provides completion evidence', async () => {
  const repo = fs.mkdtempSync(path.join(os.tmpdir(), 'cycle-backfill-'));
  try {
    const project = path.join(repo, 'projects', 'demo');
    for (const [name, kind] of [['work', 'task'], ['parent', 'epic']]) {
      const folder = path.join(project, '7-archive', name);
      fs.mkdirSync(folder, { recursive: true });
      fs.writeFileSync(path.join(folder, 'task.json'), JSON.stringify({
        id: name, kind, state: '7-archive', enteredLaneAt: '2026-10-16T12:00:00Z',
      }));
    }
    for (const args of [
      ['init', '--quiet'], ['add', '.'],
      ['-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.test', 'commit', '--quiet', '-m', 'fixture'],
    ]) {
      const result = spawnSync('git', args, { cwd: repo, encoding: 'utf8', windowsHide: true });
      assert.equal(result.status, 0, result.stderr);
    }

    const result = await run({ project, repo, write: false, verbose: false }, { write: () => {} });

    assert.deepEqual(Object.keys(result.entries), ['work']);
    assert.equal(result.stats.retiredContainers, 1);
    assert.equal(result.stats.candidates, 1);
  } finally {
    fs.rmSync(repo, { recursive: true, force: true });
  }
});

test('parseNameStatusLog reads headers, adds, renames, and deletes; tolerates CRLF and blank lines', () => {
  const text = [
    '@aaa|2026-04-26T17:40:20+02:00',
    '',
    `A\t${P}/2-ready/foo/job.json\r`,
    `M\t${P}/2-ready/foo/job.json`,
    '@bbb|2026-05-01T08:00:00+02:00',
    `R097\t${P}/2-ready/foo/job.json\t${P}/6-completed/foo/job.json`,
    '@ccc|2026-05-02T08:00:00+02:00',
    `D\t${P}/6-completed/foo/job.json`,
  ].join('\n');

  const commits = parseNameStatusLog(text);
  assert.equal(commits.length, 3);
  assert.deepEqual(commits[0].changes, [
    { action: 'A', path: `${P}/2-ready/foo/job.json` },
    { action: 'M', path: `${P}/2-ready/foo/job.json` },
  ]);
  assert.deepEqual(commits[1].changes, [
    { action: 'R', from: `${P}/2-ready/foo/job.json`, path: `${P}/6-completed/foo/job.json` },
  ]);
  assert.deepEqual(commits[2].changes, [{ action: 'D', path: `${P}/6-completed/foo/job.json` }]);
});

test('laneOf classifies lane folders and treats shards as lane-less', () => {
  assert.equal(laneOf(`${P}/6-completed/foo/job.json`, P), '6-completed');
  assert.equal(laneOf(`${P}/7-archive/foo/task.json`, P), '7-archive');
  assert.equal(laneOf(`${P}/tasks/000/ASS-324/task.json`, P), null);
  assert.equal(laneOf(`${P}/jobs/000/ASS-324/job.json`, P), null);
  assert.equal(laneOf('projects/other/6-completed/foo/job.json', P), null);
  assert.equal(laneOf(`${P}/chat/foo.json`, P), null);
});

test('buildPathEvidence follows the rename chain and dates the completion move (era migrations included)', () => {
  const commits = parseNameStatusLog([
    '@a1|2026-04-26T10:00:00Z',
    `A\t${P}/2-ready/foo/job.json`,
    '@a2|2026-05-01T10:00:00Z',
    `R097\t${P}/2-ready/foo/job.json\t${P}/6-completed/foo/job.json`,
    '@a3|2026-05-09T10:00:00Z',
    `R099\t${P}/6-completed/foo/job.json\t${P}/7-archive/foo/job.json`,
    '@a4|2026-06-01T10:00:00Z',
    `R100\t${P}/7-archive/foo/job.json\t${P}/jobs/000/ASS-1/job.json`,
    '@a5|2026-06-02T10:00:00Z',
    `R100\t${P}/jobs/000/ASS-1/job.json\t${P}/tasks/000/ASS-1/task.json`,
  ].join('\n'));

  const evidence = buildPathEvidence(commits, P);
  const entry = evidence.get(`${P}/tasks/000/ASS-1/task.json`);
  assert.ok(entry, 'final path carries the evidence');
  assert.deepEqual(entry.completedMove, { at: '2026-05-01T10:00:00Z', commit: 'a2' });
  // completed -> archive is an archive sweep, not a completion anchor
  assert.equal(entry.archiveMove, null);
});

test('buildPathEvidence: snapshot add inside a terminal lane is only an upper bound; a direct archive move is medium evidence', () => {
  const commits = parseNameStatusLog([
    '@s1|2026-04-26T10:00:00Z',
    `A\t${P}/7-archive/old-slug/job.json`,
    `A\t${P}/2-ready/live/job.json`,
    '@s2|2026-05-03T10:00:00Z',
    `R080\t${P}/2-ready/live/job.json\t${P}/7-archive/live/job.json`,
  ].join('\n'));

  const evidence = buildPathEvidence(commits, P);
  const snapshot = evidence.get(`${P}/7-archive/old-slug/job.json`);
  assert.equal(snapshot.completedMove, null);
  assert.equal(snapshot.archiveMove, null);
  assert.deepEqual(snapshot.firstTerminalSeen, { at: '2026-04-26T10:00:00Z', commit: 's1' });

  const moved = evidence.get(`${P}/7-archive/live/job.json`);
  assert.deepEqual(moved.archiveMove, { at: '2026-05-03T10:00:00Z', commit: 's2' });
  assert.equal(moved.completedMove, null);
});

test('buildPathEvidence drops deleted identities', () => {
  const commits = parseNameStatusLog([
    '@d1|2026-04-26T10:00:00Z',
    `A\t${P}/7-archive/gone/job.json`,
    '@d2|2026-04-27T10:00:00Z',
    `D\t${P}/7-archive/gone/job.json`,
  ].join('\n'));
  assert.equal(buildPathEvidence(commits, P).size, 0);
});

test('chooseEvidence prefers the completed move, then the archive move, then enteredLaneAt, then first-seen, then status mtime', () => {
  const now = Date.parse('2026-08-25T00:00:00Z');
  const full = {
    completedMove: { at: '2026-05-01T10:00:00Z', commit: 'c1' },
    archiveMove: { at: '2026-05-09T10:00:00Z', commit: 'c2' },
    enteredLaneAt: '2026-05-09T11:00:00Z',
    firstTerminalSeen: { at: '2026-04-26T10:00:00Z', commit: 'c3' },
    statusMtime: Date.parse('2026-05-08T10:00:00Z'),
  };

  assert.deepEqual(chooseEvidence(full, now), {
    completedAt: '2026-05-01T10:00:00.000Z',
    source: SOURCES.gitCompletedMove,
    confidence: 'high',
    commit: 'c1',
  });
  assert.equal(chooseEvidence({ ...full, completedMove: null }, now).source, SOURCES.gitArchiveMove);
  assert.equal(chooseEvidence({ ...full, completedMove: null, archiveMove: null }, now).source, SOURCES.taskEnteredLane);
  assert.equal(
    chooseEvidence({ ...full, completedMove: null, archiveMove: null, enteredLaneAt: null }, now).source,
    SOURCES.gitTerminalFirstSeen);
  const last = chooseEvidence({ statusMtime: full.statusMtime }, now);
  assert.equal(last.source, SOURCES.statusMtime);
  assert.equal(last.confidence, 'low');
  assert.equal(chooseEvidence({}, now), null);
});

test('chooseEvidence skips implausible timestamps instead of writing them', () => {
  const now = Date.parse('2026-08-25T00:00:00Z');
  const picked = chooseEvidence({
    completedMove: { at: '2019-01-01T00:00:00Z', commit: 'old' }, // before the plausibility floor
    archiveMove: { at: '2027-09-01T00:00:00Z', commit: 'future' }, // in the future
    enteredLaneAt: '2026-05-09T11:00:00Z',
  }, now);
  assert.equal(picked.source, SOURCES.taskEnteredLane);
  assert.equal(picked.completedAt, '2026-05-09T11:00:00.000Z');
});

test('hasLedgerCompletion detects only real lane changes into 6-completed', () => {
  const hit = '{"ts":"2026-08-01T00:00:00Z","kind":"lane_changed","details":{"from":"5-human-review","to":"6-completed"}}';
  const miss = '{"ts":"2026-08-01T00:00:00Z","kind":"lane_changed","details":{"from":"2-ready","to":"3-progress"}}';
  const decoy = '{"kind":"note","details":{"reason":"mentions lane_changed and 6-completed in prose"}}';
  assert.equal(hasLedgerCompletion([miss, hit].join('\n')), true);
  assert.equal(hasLedgerCompletion([miss, decoy, 'not json {'].join('\n')), false);
  assert.equal(hasLedgerCompletion(null), false);
});

test('buildSidecar sorts keys numerically and keeps an unchanged file byte-stable', () => {
  const entries = {
    'ASS-1000': { completedAt: '2026-05-02T00:00:00.000Z', source: 'git-archive-move', confidence: 'medium', commit: 'b' },
    'ASS-2': { completedAt: '2026-05-01T00:00:00.000Z', source: 'git-completed-move', confidence: 'high', commit: 'a' },
  };

  const first = buildSidecar(entries, null, { project: P, now: new Date('2026-08-25T10:00:00Z') });
  assert.equal(first.unchanged, false);
  assert.deepEqual(Object.keys(first.doc.entries), ['ASS-2', 'ASS-1000']);
  assert.equal(first.doc.generatedAt, '2026-08-25T10:00:00.000Z');

  // Re-run with identical evidence: unchanged, generatedAt preserved.
  const second = buildSidecar(entries, first.doc, { project: P, now: new Date('2026-08-26T10:00:00Z') });
  assert.equal(second.unchanged, true);
  assert.equal(second.doc.generatedAt, '2026-08-25T10:00:00.000Z');

  // New evidence invalidates the unchanged short-circuit.
  const revised = { ...entries, 'ASS-3': { completedAt: '2026-05-03T00:00:00.000Z', source: 'status-mtime', confidence: 'low' } };
  const third = buildSidecar(revised, first.doc, { project: P, now: new Date('2026-08-26T10:00:00Z') });
  assert.equal(third.unchanged, false);
  assert.equal(third.doc.generatedAt, '2026-08-26T10:00:00.000Z');
});

test('entriesEqual compares the audit-relevant fields only', () => {
  const a = { 'K-1': { completedAt: 'x', source: 's', confidence: 'c', commit: 'z' } };
  assert.equal(entriesEqual(a, { 'K-1': { completedAt: 'x', source: 's', confidence: 'c', commit: 'z' } }), true);
  assert.equal(entriesEqual(a, { 'K-1': { completedAt: 'x', source: 's', confidence: 'c' } }), false);
  assert.equal(entriesEqual(a, {}), false);
  assert.equal(entriesEqual({}, {}), true);
});
