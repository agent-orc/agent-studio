import { test } from 'node:test';
import assert from 'node:assert/strict';
import { COMMANDS } from '../src/commands.mjs';

test('routing drill only reads the installation and reports catalogue gaps', async () => {
  const calls = [];
  const report = { dryRun: true, passed: true, shortStepReservePoints: 10, cases: [],
    missingEquivalence: [{ consumer: 'review-grade', stepId: 'post-code-review-grade',
      preferred: { cliType: 'claude', model: 'unqualified-model', thinkingLevel: 'low' } }] };
  const result = await COMMANDS['routing drill'].run({ client: { async request(...args) {
    calls.push(args); return report;
  } } }, [], { project: 'sample', dryRun: true });
  assert.deepEqual(calls, [['GET', '/api/model-selection/drill', { query: { project: 'sample', pipelineType: undefined } }]]);
  assert.equal(result.data, report);
  assert.match(result.text, /no model tokens/);
  assert.match(result.text, /Missing equivalence: post-code-review-grade/);
});
