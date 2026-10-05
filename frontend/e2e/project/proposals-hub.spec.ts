import { test, expect, type Page } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';

const projectName = 'Proposal Demo';
const results = process.env.RESULTS_DIR ?? path.join(process.cwd(), 'test-results');
const evidence = fs.readFileSync(path.join(process.cwd(), '..', 'docs', 'proposals', '2026-07-11', 'assets', '001-crash-recovery-modal-with-49-files.png'));

async function routes(page: Page, evidenceDelayMs = 0): Promise<void> {
  await page.route('**/api/**', route => route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => undefined));
  await page.route('**/api/v1/workspaces**', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([{ id: 'WS-PROP', displayName: 'Review', sortOrder: 0, isDefault: true, projects: [{ id: 'PROJ-PROP', displayName: projectName, shortCode: 'PRP', workspaceId: 'WS-PROP', storageLocation: 'C:/fixtures/proposals', archived: false, urls: [] }] }]) }));
  await page.route('**/api/watch-paths**', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([{ name: projectName, path: 'C:/fixtures/proposals', rootPath: 'C:/fixtures/proposals' }]) }));
  await page.route('**/api/v1/studio/board**', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ backlog: [], preparation: [], ready: [], progress: [], autoReview: [], humanReview: [], completed: [], archive: [] }) }));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ projects: {} }) }));
  await page.route('**/api/cli/quota**', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: '2026-07-11T08:00:00Z', ttlSeconds: 600, snapshots: [] }) }));
  await page.route('**/api/cli/usage**', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: '2026-07-11T08:00:00Z', sessions: [] }) }));
  await page.route('**/api/projects/Proposal%20Demo/proposals', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ items: [
    { id: 'survey-001', generation: '2026-07-11', finding: 'The project navigation collapses at narrow widths and hides the operator decision context.', evidenceScreenshot: '2026-07-11/assets/001.png', proposal: 'Keep the navigation and decision controls visible at narrow widths.', estimatedEffort: 'medium', severity: 'critical', status: 'proposed', spawnedTask: null, topic: 'Responsiveness', categories: ['responsiveness', 'navigation'], source: 'Visual survey: narrow-board.png', rejectionReason: null, rejectionReasonRaw: null, relPath: '2026-07-11/survey-001.md', updatedAt: '2026-07-11T08:00:00Z' },
    { id: 'survey-002', generation: '2026-07-11', finding: 'The settings view lacks a concise summary before its long form.', evidenceScreenshot: '2026-07-11/assets/002.png', proposal: 'Add a compact settings summary with direct links to the affected sections.', estimatedEffort: 'small', severity: 'medium', status: 'proposed', spawnedTask: null, topic: 'Information architecture', categories: ['settings'], source: 'Visual survey: settings.png', rejectionReason: null, rejectionReasonRaw: null, relPath: '2026-07-11/survey-002.md', updatedAt: '2026-07-11T08:00:00Z' },
    { id: 'survey-003', generation: '2026-06-18', finding: 'A prior generation item has already been implemented.', evidenceScreenshot: '2026-06-18/assets/003.png', proposal: 'Group the visual evidence by project surface.', estimatedEffort: 'medium', severity: 'medium', status: 'spawned', spawnedTask: null, topic: 'Visual evidence', categories: ['evidence'], source: 'Visual survey: evidence.png', rejectionReason: null, rejectionReasonRaw: null, relPath: '2026-06-18/survey-003.md', updatedAt: '2026-07-01T08:00:00Z' },
  ] }) }));
  await page.route('**/api/projects/Proposal%20Demo/proposals/evidence/**', async route => {
    if (evidenceDelayMs) await new Promise(resolve => setTimeout(resolve, evidenceDelayMs));
    await route.fulfill({ status: 200, contentType: 'image/png', body: evidence });
  });
}

test('Project Hub proposals render in both themes', async ({ page }) => {
  fs.mkdirSync(results, { recursive: true });
  await routes(page);
  await page.addInitScript(name => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({ v: 1, tabs: [{ kind: 'hub', projectName: name, section: 'proposals' }], activeKey: `hub:${name}` })), projectName);
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto('/');
  await expect(page.getByTestId('project-proposals-panel')).toBeVisible({ timeout: 30_000 });
  await expect(page.getByTestId('proposal-approve')).toBeVisible();
  await expect(page.getByTestId('proposal-topic')).toContainText('Responsiveness');
  await expect(page.getByTestId('proposal-new')).toBeVisible();
  for (const theme of ['dark', 'light'] as const) {
    await page.evaluate(value => { document.documentElement.dataset['studioTheme'] = value; }, theme);
    await page.waitForTimeout(200);
    await page.screenshot({ path: path.join(results, `project-proposals-${theme}--mocked.png`), fullPage: false });
  }
});

test('Project proposal evidence reports progress while the prioritized image loads', async ({ page }) => {
  await routes(page, 700);
  await page.addInitScript(name => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({ v: 1, tabs: [{ kind: 'hub', projectName: name, section: 'proposals' }], activeKey: `hub:${name}` })), projectName);
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto('/');

  await expect(page.getByTestId('proposal-image-loading')).toContainText('Loading evidence');
  await expect(page.locator('.proposal-detail__image--loaded')).toBeAttached({ timeout: 5_000 });
  await expect(page.getByTestId('proposal-image-loading')).toBeHidden();
  await expect(page.getByLabel('Proposal metadata')).toContainText('Generation');
  await expect(page.getByTestId('proposal-approve')).toHaveText('Approve and create card');
});

test('Project proposal rejection explains and records the CLI-refined reason', async ({ page }) => {
  await routes(page);
  await page.route('**/api/projects/Proposal%20Demo/proposals/refine-feedback', route => route.fulfill({
    status: 200, contentType: 'application/json', body: JSON.stringify({ refinedFeedback: 'Make Responsiveness explicit as the governing topic.' }),
  }));
  await page.route('**/api/projects/Proposal%20Demo/proposals/survey-001/decision', async route => {
    const body = route.request().postDataJSON();
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ proposal: {
      id: 'survey-001', generation: '2026-07-11', finding: 'Narrow layout clips content.', evidenceScreenshot: '2026-07-11/assets/001.png', proposal: 'Keep controls visible.', estimatedEffort: 'medium', severity: 'critical', status: 'rejected', spawnedTask: null, topic: 'Responsiveness', categories: ['responsiveness'], source: 'Visual survey: narrow-board.png', rejectionReason: body.rejectionReason, rejectionReasonRaw: body.rejectionReasonRaw, relPath: '2026-07-11/survey-001.md', updatedAt: '2026-07-13T08:00:00Z',
    } }) });
  });
  await page.addInitScript(name => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({ v: 1, tabs: [{ kind: 'hub', projectName: name, section: 'proposals' }], activeKey: `hub:${name}` })), projectName);
  await page.goto('/');

  await page.getByTestId('proposal-reject').click();
  await page.getByLabel('Original feedback').fill('Da muss ganz klar Responsiveness stehen.');
  await page.getByRole('button', { name: 'Refine with CLI' }).click();
  await expect(page.getByLabel('Refined historical record')).toHaveValue('Make Responsiveness explicit as the governing topic.');
  await page.getByTestId('proposal-reject-confirm').click();
  await expect(page.getByText('Decision recorded: rejected')).toBeVisible();
  await expect(page.getByText('Make Responsiveness explicit as the governing topic.')).toBeVisible();
});
