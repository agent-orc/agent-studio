import { expect, test, type Page, type Route } from '@playwright/test';
import { mkdirSync } from 'fs';
import * as path from 'path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

// AGT-3681 (playthrough AGT-3648 S1-F1): a batch manifest that does not
// deserialize is quarantined. The operator sees one "Batch record unreadable"
// attention item naming the batch, project, file, parse error and next step,
// and the batch panel lists the record while the readable batches stay visible.
test.use({ serviceWorkers: 'block', reducedMotion: 'reduce' });

const PROJECT = 'quarantine-fixture';
const RESULTS_DIR = process.env.JOB_RESULTS_DIR?.trim()
  ? path.resolve(process.env.JOB_RESULTS_DIR)
  : path.resolve('test-results', 'batch-record-quarantine');
const VIEWPORTS = [
  { width: 1280, height: 900 },
  { width: 390, height: 844 },
] as const;
const THEMES = ['light', 'dark'] as const;

const FILE = '/var/lib/agentstudio/batch-gates/5f0c2a/manifest.json';
const ERROR = "Expected depth to be zero at the end of the JSON payload. There is an open JSON object or array that "
  + "should be closed. Path: $.members[0] | LineNumber: 30 | BytePositionInLine: 7.";
const NEXT_STEP = 'Restore this file from a backup, or move the batch folder /var/lib/agentstudio/batch-gates/5f0c2a '
  + 'out of the batch store. Until then the pilot skips batch batch-stale; every other batch keeps running.';

const quarantined = {
  batchId: 'batch-stale', project: PROJECT, kind: 'manifest', file: FILE, error: ERROR,
  detectedAtUtc: new Date(Date.now() - 4 * 60_000).toISOString(), nextStep: NEXT_STEP,
};

const attention = {
  items: [{
    id: `batch.record-unreadable|${FILE}|2026-11-09T21:14:00.0000000Z`,
    type: 'batch.record-unreadable', project: PROJECT, cardKey: null, cards: [],
    reason: `Batch record unreadable: the manifest record of batch batch-stale in project ${PROJECT} does not read `
      + `(${FILE}): ${ERROR} Next step: ${NEXT_STEP}`,
    observedAt: quarantined.detectedAtUtc, suggestedOperations: ['batch.triage'],
  }],
  count: 1,
};

const batchState = {
  plan: { decision: 'wait', reason: 'awaiting planner tick', targetSize: 2, closeAtUtc: null,
    proposedMembers: ['AGT-21', 'AGT-22'], upcoming: [], advisor: null },
  pending: [],
  batches: [{
    batchId: 'batch-running', parentBatchId: null, phase: 'abandoned',
    phaseReason: 'interrupted before a publishable verdict; reconstruct from the closed snapshot',
    baseSha: 'b'.repeat(40), candidateSha: null, runs: [],
    members: [{ taskKey: 'AGT-21', replay: null, released: false }, { taskKey: 'AGT-22', replay: null, released: false }],
  }],
  members: {},
  isolationFindings: [],
  quarantined: [quarantined],
};

const json = (route: Route, body: unknown) =>
  route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });

async function installRoutes(page: Page): Promise<void> {
  await page.route('**/api/operator/attention', route => json(route, attention));
  // The Needs you count also reads Dossiers, backups and credentials; keep them quiet.
  await page.route('**/api/workbenches', route => json(route, { items: [] }));
  await page.route('**/api/operator/backups', route => json(route, {
    state: 'ok', stale: false, generatedAt: quarantined.detectedAtUtc, warnings: [], backups: [], offHost: {},
  }));
  await page.route('**/api/v1/management/credential-inventory', route => json(route, {
    generatedAt: quarantined.detectedAtUtc, items: [],
  }));
  await page.route(`**/api/projects/${PROJECT}/batch-gate/state**`, route => json(route, batchState));
  await page.route(`**/api/projects/${PROJECT}/batch-gate/failed-tests**`, route => json(route, {
    project: PROJECT, windowDays: 7, pendingEvidenceRuns: 0, tests: [],
  }));
}

async function screenshot(page: Page, name: string): Promise<void> {
  await page.waitForTimeout(150);
  await page.screenshot({ path: path.join(RESULTS_DIR, `${name}.png`), animations: 'disabled' });
}

test.beforeEach(() => { mkdirSync(RESULTS_DIR, { recursive: true }); });

test('an unreadable batch record raises one attention item and the batch panel lists it', async ({ page }) => {
  await installRoutes(page);
  await page.goto('/');
  await dismissDevErrorDialog(page);
  await expect(page.getByTestId('studio-attention-count')).toHaveText('1', { timeout: 30_000 });

  for (const viewport of VIEWPORTS) {
    await page.setViewportSize(viewport);
    for (const theme of THEMES) {
      await setTheme(page, theme);
      await page.getByTestId('studio-attention-trigger').click();
      const list = page.getByTestId('studio-attention-list');
      const group = list.getByTestId('attention-group-batch-record');
      await expect(group).toBeVisible();
      await expect(group.locator('h3')).toContainText('Batch record unreadable');
      const row = group.getByTestId('attention-row');
      await expect(row).toHaveCount(1);
      const reason = row.getByTestId('attention-row-reason');
      for (const part of ['batch-stale', PROJECT, FILE, 'Expected depth to be zero', 'Next step: Restore this file'])
        await expect(reason).toContainText(part);
      await expect(row.getByTestId('attention-row-action')).toHaveText('Open batch gate');
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
      await screenshot(page, `quarantine-attention-${viewport.width}-${theme}`);
      await page.keyboard.press('Escape');
      await expect(list).toBeHidden();
    }
  }

  await page.setViewportSize(VIEWPORTS[0]);
  await page.getByTestId('studio-attention-trigger').click();
  await page.getByTestId('attention-group-batch-record').getByTestId('attention-row-action').click();
  const panel = page.getByTestId('batch-panel');
  await expect(panel).toBeVisible();
  const note = panel.getByTestId('batch-quarantine');
  await expect(note).toBeVisible();
  await expect(note.getByTestId('status-indicator')).toHaveAttribute('data-tone', 'warning');
  await expect(note).toContainText('Batch record unreadable');
  const record = note.getByTestId('batch-quarantine-row');
  await expect(record).toHaveCount(1);
  await expect(record).toHaveAttribute('data-kind', 'manifest');
  await expect(record).toContainText('Manifest of batch batch-stale');
  await expect(record).toContainText(`project ${PROJECT}`);
  await expect(record).toContainText(FILE);
  await expect(record).toContainText('Expected depth to be zero');
  await expect(record).toContainText(NEXT_STEP);
  // The readable batch stays in the panel next to the quarantine note.
  await expect(panel.locator('#batch-batch-running')).toContainText('interrupted before a publishable verdict');

  for (const viewport of VIEWPORTS) {
    await page.setViewportSize(viewport);
    for (const theme of THEMES) {
      await setTheme(page, theme);
      await note.scrollIntoViewIfNeeded();
      const box = await record.boundingBox();
      expect(box && box.x >= 0 && box.x + box.width <= viewport.width + 1).toBe(true);
      await screenshot(page, `quarantine-batch-panel-${viewport.width}-${theme}`);
    }
  }
});
