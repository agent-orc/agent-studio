import assert from 'node:assert/strict';
import { writeFile } from 'node:fs/promises';
import { test } from 'node:test';
import { runUnitTests } from './run-unit-tests.mjs';

test('a completed file report supplies a result when the runner prints no summary', async () => {
  const lines = [];
  const code = await runUnitTests([], async (_args, reportPath) => {
    await writeFile(reportPath, JSON.stringify({
      numTotalTests: 2,
      numPassedTests: 2,
      numFailedTests: 0,
      testResults: [{ name: 'long-generated-spec-name.js', status: 'passed' }],
    }));
    return 0;
  }, line => lines.push(line));
  assert.equal(code, 0);
  assert.deepEqual(lines, ['Test Files  1 passed (1)\nTests  2 passed (2)']);
});

test('an exit-zero command without a report fails with a diagnostic', async () => {
  const code = await runUnitTests([], async () => 0, () => assert.fail('no summary expected'));
  assert.equal(code, 2);
});

test('an empty report cannot turn an unrun suite green', async () => {
  const code = await runUnitTests([], async (_args, reportPath) => {
    await writeFile(reportPath, JSON.stringify({
      numTotalTests: 0,
      numPassedTests: 0,
      numFailedTests: 0,
      testResults: [],
    }));
    return 0;
  }, () => assert.fail('no summary expected'));
  assert.equal(code, 2);
});
