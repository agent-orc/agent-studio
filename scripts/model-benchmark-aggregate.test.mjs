import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { promisify } from 'node:util';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

import { aggregateTasks, loadTaskStore, normalizeTask, renderMarkdown } from './model-benchmark-aggregate.mjs';

const execFileAsync = promisify(execFile);
const scriptPath = path.join(path.dirname(fileURLToPath(import.meta.url)), 'model-benchmark-aggregate.mjs');

function transition(lane, atUtc) {
  return { lane, atUtc };
}

test('aggregates grades, reissues, duration, tokens, and terminal lanes', () => {
  const raw = [
    {
      key: 'AGT-1', model: 'gpt-5.6-sol', thinkingLevel: 'xhigh', cliType: 'codex', taskType: 'feature',
      state: '6-completed', orchestratorVerdict: { verdict: 'accept', grade: 'B' },
      tokenSummary: { totalTokens: 1_000 },
      provenance: { transitions: [
        transition('3-progress', '2026-07-01T10:00:00Z'),
        transition('4-auto-review', '2026-07-01T10:10:00Z'),
        transition('3-progress', '2026-07-01T10:20:00Z'),
        transition('6-completed', '2026-07-01T10:40:00Z'),
      ] },
    },
    {
      key: 'AGT-2', model: 'gpt-5.6-sol', thinkingLevel: 'xhigh', cliType: 'codex', taskType: 'feature',
      state: '5e-escalated', tags: ['code-review:grade-d'], orchestratorVerdict: 'escalate',
      lastUsage: { tokens: '3k tokens' },
      provenance: { transitions: [
        transition('3-progress', '2026-07-01T11:00:00Z'),
        transition('5e-escalated', '2026-07-01T11:20:00Z'),
      ] },
    },
  ];
  const tasks = raw.map((task, index) => normalizeTask(task, `/store/projects/agt/tasks/000/AGT-${index + 1}/task.json`));
  const report = aggregateTasks(tasks, { source: { mode: 'test', label: 'fixture' }, discovered: 2, warnings: [] });
  const group = report.groups[0];

  assert.equal(group.runCount, 2);
  assert.deepEqual(group.gradeDistribution, { A: 0, B: 1, C: 0, D: 1, unknown: 0 });
  assert.equal(group.reissueCount, 1);
  assert.equal(group.reissueRate, 0.5);
  assert.equal(group.reissueRoundsTotal, 1);
  assert.equal(group.medianDurationSeconds, 1_800);
  assert.equal(group.medianTokenUsage, 2_000);
  assert.equal(group.abortCount, 1);
  assert.deepEqual(group.laneEndStateDistribution, { '5e-escalated': 1, '6-completed': 1 });
});

test('reads central and registry-linked stores, skips malformed tasks, and normalizes legacy task types', async () => {
  const temporary = await mkdtemp(path.join(os.tmpdir(), 'model-benchmark-store-'));
  try {
    const centralTask = path.join(temporary, 'projects', 'PROJ-001', 'tasks', '000', 'ONE-1');
    const external = path.join(temporary, '..', `${path.basename(temporary)}-external`);
    const externalTask = path.join(external, '6-completed', 'two');
    await mkdir(path.join(temporary, '.metadata'), { recursive: true });
    await mkdir(centralTask, { recursive: true });
    await mkdir(externalTask, { recursive: true });
    await writeFile(path.join(temporary, '.metadata', 'projects.json'), JSON.stringify({
      Projects: [{ StorageLocation: external }],
    }));
    await writeFile(path.join(centralTask, 'task.json'), JSON.stringify({
      key: 'ONE-1', model: 'gpt-5.4-mini', thinkingLevel: 'medium', taskType: 'user-story', state: '6-completed',
    }));
    await writeFile(path.join(externalTask, 'task.json'), JSON.stringify({
      key: 'TWO-1', model: 'claude-opus-4-8', thinkingLevel: 'high', taskType: 'chore', state: '6-completed',
    }));
    const malformed = path.join(temporary, 'projects', 'PROJ-001', 'tasks', '000', 'BROKEN');
    await mkdir(malformed, { recursive: true });
    await writeFile(path.join(malformed, 'task.json'), '{');

    const loaded = await loadTaskStore(temporary);
    assert.equal(loaded.discovered, 3);
    assert.equal(loaded.parseErrors, 1);
    assert.deepEqual(loaded.tasks.map(task => task.taskType).sort(), ['chore', 'feature']);
  } finally {
    await rm(temporary, { recursive: true, force: true });
    await rm(`${temporary}-external`, { recursive: true, force: true });
  }
});

test('CLI output is byte-identical on repeated runs and markdown carries required model rows', async () => {
  const temporary = await mkdtemp(path.join(os.tmpdir(), 'model-benchmark-cli-'));
  try {
    const snapshot = path.join(temporary, 'tasks.json');
    const output = path.join(temporary, 'results');
    await writeFile(snapshot, JSON.stringify([
      { key: 'A', model: 'gpt-5.6-sol', thinkingLevel: 'xhigh', taskType: 'feature', state: '6-completed' },
      { key: 'B', model: 'gpt-5.6-sol', thinkingLevel: 'medium', taskType: 'chore', state: '6-completed' },
      { key: 'C', model: 'claude-opus-4-8', thinkingLevel: 'high', taskType: 'chore', state: '6-completed' },
      { key: 'D', model: 'gpt-5.4-mini', thinkingLevel: 'medium', taskType: 'feature', state: '6-completed' },
    ]));
    const args = [scriptPath, '--snapshot', snapshot, '--source-label', 'fixture', '--output-dir', output];
    const executionOptions = {
      env: { ...process.env, TASK_REPOSITORY: '/unused/when-snapshot-is-explicit' },
      windowsHide: true,
    };
    await execFileAsync(process.execPath, args, { ...executionOptions, windowsHide: true });
    const firstJson = await readFile(path.join(output, 'model-benchmark.json'), 'utf8');
    const firstMarkdown = await readFile(path.join(output, 'model-benchmark.md'), 'utf8');
    await execFileAsync(process.execPath, args, { ...executionOptions, windowsHide: true });
    assert.equal(await readFile(path.join(output, 'model-benchmark.json'), 'utf8'), firstJson);
    assert.equal(await readFile(path.join(output, 'model-benchmark.md'), 'utf8'), firstMarkdown);
    assert.match(firstMarkdown, /\| gpt-5\.6-sol \| xhigh \|/);
    assert.match(firstMarkdown, /\| gpt-5\.6-sol \| medium \|/);
    assert.match(firstMarkdown, /\| claude-opus-4-8 \| high \|/);
    assert.match(firstMarkdown, /\| gpt-5\.4-mini \| medium \|/);
  } finally {
    await rm(temporary, { recursive: true, force: true });
  }
});

test('markdown states observational limitations and sample coverage', () => {
  const task = normalizeTask({
    key: 'A', model: 'test-model', thinkingLevel: 'medium', taskType: 'chore', state: '2-ready',
  }, '/store/projects/test/task.json');
  const report = aggregateTasks([task], { source: { mode: 'test', label: 'fixture' }, warnings: [] });
  const markdown = renderMarkdown(report);
  assert.match(markdown, /observational history, not a controlled fresh-run benchmark/);
  assert.match(markdown, /token medians are based only on available samples/);
});

test('does not count untouched backlog and ready configurations as runs', () => {
  const configured = normalizeTask({
    key: 'A', model: 'gpt-5.6-sol', thinkingLevel: 'medium', taskType: 'feature', state: '2-ready',
  }, '/store/projects/test/task.json');
  const reissuedToReady = normalizeTask({
    key: 'B', model: 'gpt-5.6-sol', thinkingLevel: 'medium', taskType: 'feature', state: '2-ready',
    provenance: { transitions: [transition('3-progress', '2026-07-01T10:00:00Z'), transition('2-ready', '2026-07-01T10:10:00Z')] },
  }, '/store/projects/test/task.json');
  const report = aggregateTasks([configured, reissuedToReady], { source: { mode: 'test', label: 'fixture' }, warnings: [] });
  assert.equal(report.summary.taskRecordsIncluded, 1);
  assert.equal(report.summary.taskRecordsExcludedWithoutRunEvidence, 1);
  assert.equal(report.groups[0].runCount, 1);
});
