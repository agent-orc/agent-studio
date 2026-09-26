import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';

test('reducer keeps missing Git correlation distinct from observed zero spawns', () => {
  const dir = mkdtempSync(join(tmpdir(), 'task-switch-reducer-'));
  try {
    const capture = join(dir, 'capture.json');
    const trace = join(dir, 'trace.jsonl');
    const summary = join(dir, 'summary.json');
    writeFileSync(capture, JSON.stringify({
      sourceRevision: 'test-revision', runEnvironment: 'test',
      samples: [
        { cohort: 'board-click', sampleClass: 'small', switchId: 'switch-1',
          outcome: 'ok', paintOpportunityMs: 12, domReadyMs: 10,
          domWorkMs: 3, markdownConversion: [{ name: 'markdown', durationMs: 2 }] },
        { cohort: 'deep-link', sampleClass: 'archived', switchId: 'switch-2',
          outcome: 'error' },
      ],
    }));
    writeFileSync(trace, [
      'task-switch-trace {"requestId":"request-1","switchId":"switch-1","gitSpawns":0,"gitTimeouts":0,"stages":{"index.lookup":{"ms":4}}}',
      'task-switch-trace {"requestId":"request-2","switchId":"switch-1","gitSpawns":0,"gitTimeouts":0,"stages":{"index.lookup":{"ms":3}}}',
      '',
    ].join('\n'));
    execFileSync(process.execPath, [new URL('./reduce-switch-capture.mjs', import.meta.url).pathname,
      capture, trace, summary]);
    const result = JSON.parse(readFileSync(summary, 'utf8'));
    assert.equal(result.cohorts['board-click'].gitSpawns, 0);
    assert.equal(result.cohorts['board-click'].tracedRequests, 2);
    assert.equal(result.cohorts['board-click'].domWorkP50Ms, 3);
    assert.equal(result.cohorts['board-click'].markdownP50Ms, 2);
    assert.equal(result.cohorts['board-click'].stageStats['index.lookup'].sampleCount, 1);
    assert.equal(result.cohorts['board-click'].stageStats['index.lookup'].p95Ms, 7);
    assert.equal(result.cohorts['deep-link'].gitSpawns, null);
    assert.equal(result.cohorts['deep-link'].gitTimeouts, null);
    assert.equal(result.cohorts['deep-link'].errors, 1);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});
