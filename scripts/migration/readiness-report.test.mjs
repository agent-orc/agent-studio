// Run: node --test scripts/migration/readiness-report.test.mjs
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { formatReadiness, runCli } from './readiness-report.mjs';

const report = {
  computedAt: '2026-11-01T12:04:00+00:00',
  state: 'fail',
  failing: 1,
  attention: 2,
  checks: [
    { id: 'git-identity', title: 'Git identity of the service user', scope: 'server', subject: null, state: 'pass',
      meaning: 'Commits are made as Agent Studio <studio@agent-studio.invalid>.', remedy: null },
    { id: 'repository-visibility', title: 'Repository visibility known', scope: 'project', subject: 'Demo', state: 'warn',
      meaning: 'Not recorded.', remedy: 'Record it: PUT /api/projects/Demo/repository-visibility with {"visibility":"private"}.' },
    { id: 'committer-identity', title: 'Committer identity (git commit-tree probe)', scope: 'server', subject: null, state: 'fail',
      meaning: 'git commit-tree failed in a scratch repository (Committer identity unknown).',
      remedy: 'In the stack directory run: docker compose exec api git config --global user.name "Agent Studio"' },
    { id: 'gate-worker', title: 'Gate worker reachable', scope: 'server', subject: 'kori-gate', state: 'unknown',
      meaning: 'No gate reached SSH gate host kori-gate in the last 5 minutes.', remedy: 'The next gate proves it.' },
  ],
};

test('failing lines come first with their meaning and remedy; passing lines are counted only', () => {
  const { text, failing } = formatReadiness(report);
  const lines = text.split('\n');

  assert.equal(failing, 1);
  assert.equal(lines[0], 'readiness: 1 failing, 2 need attention, 4 checks (computed 2026-11-01T12:04:00+00:00)');
  assert.equal(lines[1], '  FAIL committer-identity: Committer identity (git commit-tree probe)');
  assert.equal(lines[2], '       git commit-tree failed in a scratch repository (Committer identity unknown).');
  assert.equal(lines[3], '       remedy: In the stack directory run: docker compose exec api git config --global user.name "Agent Studio"');
  assert.equal(lines[4], '  WARN repository-visibility [Demo]: Repository visibility known');
  assert.equal(lines[7], '  UNKNOWN gate-worker [kori-gate]: Gate worker reachable');
  assert.ok(!text.includes('git-identity'));
});

test('a ready server prints one line', () => {
  const ready = { ...report, checks: [report.checks[0]] };

  assert.deepEqual(formatReadiness(ready), { text: 'readiness: all 1 checks pass (computed 2026-11-01T12:04:00+00:00)', failing: 0 });
});

test('a pending report says so', () => {
  const { text } = formatReadiness({ computedAt: null, state: 'pending', checks: [] });

  assert.match(text, /has not computed its readiness report yet/);
});

test('the command exits 4 when a check fails, 0 when none does, and 2 for an unreadable report', () => {
  const out = [];
  const write = text => out.push(text);

  assert.equal(runCli(['r.json'], { read: () => JSON.stringify(report), write }), 4);
  assert.equal(runCli(['r.json'], { read: () => JSON.stringify({ ...report, checks: [report.checks[1]] }), write }), 0);
  assert.equal(runCli(['r.json'], { read: () => '<html>', write }), 2);
  assert.match(out.at(-1), /^readiness: the report could not be read/);
});
