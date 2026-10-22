import { test, expect } from '../fixtures/dev-backend';
import { mkdirSync } from 'node:fs';
import path from 'node:path';

const shots = process.env.JOB_RESULTS_DIR?.trim()
  ? path.join(process.env.JOB_RESULTS_DIR, 'screenshots') : 'test-results';
mkdirSync(shots, { recursive: true });

test('workspace rules can be drafted, replayed and activated with a reason', async ({ page, devBackend }) => {
  expect(devBackend.port).toBeGreaterThan(0);
  let active = false;
  const version = {
    versionId: 'drv_test', parentVersionId: null, actor: 'operator', reason: 'Watch queue pressure',
    diff: 'queue-pressure', createdAt: '2026-10-15T10:00:00Z', rules: [{
      id: 'queue-pressure', trigger: 'capacity-check', factId: 'host.queue', warningThreshold: null,
      candidateActions: ['observe'], autonomyClass: 'A1', model: 'gpt-5.6-sol',
      missingDataBehavior: 'ask', freshnessMinutes: 60,
    }],
  };
  await page.route(/\/api\/v1\/workspaces(?:\?.*)?$/, route => route.fulfill({ json: [{
    id: 'ws-test', displayName: 'Test workspace', sortOrder: 0, isDefault: true,
    color: null, createdAt: '2026-10-15T10:00:00Z', projects: [],
  }] }));
  await page.route(/\/api\/workspaces\/ws-test\/decision-rules(?:\?.*)?$/, route =>
    route.fulfill({ json: { activeVersionId: active ? version.versionId : null,
      versions: active ? [version] : [] } }));
  await page.route(/\/api\/workspaces\/ws-test\/decision-rules\/versions(?:\?.*)?$/, route =>
    route.fulfill({ json: version }));
  await page.route(/\/api\/workspaces\/ws-test\/decision-rules\/versions\/drv_test\/dry-run(?:\?.*)?$/,
    route => route.fulfill({ json: { replayed: 1, floorConflicts: [], occurrences: [{
      receiptId: 'odr_test', occurrenceId: 'capacity:host-1', oldSignals: [],
      newSignals: ['queue-pressure: 6 (warning)'], oldCandidates: [],
      newCandidates: ['observe'], observedOutcome: 'recorded', missingEvidence: false,
      modelResultLabel: 'counterfactual-model-result',
    }] } }));
  await page.route(/\/api\/workspaces\/ws-test\/decision-rules\/versions\/drv_test\/activate(?:\?.*)?$/,
    route => { active = true; return route.fulfill({ json: { versionId: version.versionId } }); });

  await page.goto('/#/workspace/settings/orchestrator');
  const panel = page.getByTestId('decision-rules-panel');
  await expect(panel).toBeVisible();
  await expect(panel).toContainText('platform defaults');
  await panel.getByRole('button', { name: 'Add rule' }).click();
  await expect(panel.getByLabel('Trigger').locator('option[value="deep-review"]')).toHaveCount(1);
  await panel.getByLabel('Trigger').selectOption('deep-review');
  await expect(panel.getByLabel('Review reasoning')).toBeVisible();
  await panel.getByLabel('Review reasoning').selectOption('xhigh');
  await panel.getByLabel('Trigger').selectOption('capacity-check');
  await panel.getByLabel('Rule id').fill('queue-pressure');
  await panel.getByLabel('Fact id').fill('host.queue');
  await expect(panel.getByRole('button', { name: 'Save draft' })).toBeDisabled();
  await panel.getByLabel('Operator reason').fill('Watch queue pressure');
  await panel.getByRole('button', { name: 'Save draft' }).click();
  await panel.getByRole('button', { name: 'Dry run draft' }).click();
  await expect(panel.getByTestId('decision-rules-dry-run')).toContainText('counterfactual');
  await panel.getByRole('button', { name: 'Activate draft' }).click();
  await expect(panel).toContainText('Active version: drv_test');
  await panel.screenshot({ path: path.join(shots, 'decision-rules-light.png') });
  await page.evaluate(() => { document.documentElement.dataset['studioTheme'] = 'dark'; });
  await panel.screenshot({ path: path.join(shots, 'decision-rules-dark.png') });
});
