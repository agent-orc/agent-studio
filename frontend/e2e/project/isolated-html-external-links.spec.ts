import { expect, test } from '../fixtures/dev-backend';
import type { FrameLocator, Page, TestInfo } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';

test.use({ serviceWorkers: 'block' });

const REPOSITORY_ROOT = path.resolve(__dirname, '..', '..', '..');
const EXTERNAL_LINK = /<a\s[^>]*href="https:\/\//i;

interface DossierCandidate { projectName: string; id: string; entryPath: string }

function evidencePath(testInfo: TestInfo, fileName: string): string {
  const jobResultsDir = process.env['JOB_RESULTS_DIR']?.trim();
  if (!jobResultsDir) return testInfo.outputPath(fileName);
  const resultsDir = path.resolve(jobResultsDir);
  fs.mkdirSync(resultsDir, { recursive: true });
  return path.join(resultsDir, fileName);
}

function slugFor(name: string): string {
  return name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
}

async function mockSlowBootProbes(page: Page): Promise<void> {
  await page.route('**/healthz', route => route.fulfill({ status: 200, body: 'Healthy' }));
  await page.route(/\/api\/cli\/[^/]+\/models$/, route => route.fulfill({
    status: 200, contentType: 'application/json', body: JSON.stringify({ models: [], source: 'external-links-e2e' }),
  }));
  await page.route('**/api/cli/quota', route => route.fulfill({
    status: 200, contentType: 'application/json',
    body: JSON.stringify({ at: new Date().toISOString(), ttlSeconds: 600, snapshots: [] }),
  }));
  await page.route('**/api/cli/usage', route => route.fulfill({
    status: 200, contentType: 'application/json', body: JSON.stringify({ at: new Date().toISOString(), sessions: [] }),
  }));
  await page.route('**/api/crash-recovery/pending', route => route.fulfill({
    status: 200, contentType: 'application/json', body: JSON.stringify({ pending: [] }),
  }));
}

/**
 * Clicks the first external link inside the frame and walks the shared
 * confirm dialog: Cancel keeps the operator in Studio, Open page opens the
 * target in a new tab.
 */
async function confirmExternalLink(page: Page, frame: FrameLocator, screenshot: string): Promise<void> {
  const link = frame.locator('a[href^="https://"]:visible').first();
  await expect(link).toBeVisible({ timeout: 15_000 });
  const href = (await link.getAttribute('href'))!;
  const host = new URL(href).host;

  let popups = 0;
  page.context().on('page', () => { popups += 1; });

  await link.click();
  const dialog = page.getByTestId('confirm-dialog');
  await expect(dialog).toBeVisible();
  await expect(dialog).toContainText(`Open ${host}?`);
  await expect(page.getByTestId('confirm-dialog-message')).toHaveText(
    `This link leaves Agent Studio and opens a page on ${host} in a new tab.`);
  await expect(page.getByTestId('confirm-dialog-detail')).toHaveText(new URL(href).href);
  await page.screenshot({ path: screenshot, fullPage: false });

  await page.getByTestId('confirm-dialog-cancel').click();
  await expect(dialog).toBeHidden();
  await page.waitForTimeout(300);
  expect(popups).toBe(0);

  await link.click();
  const popup = page.context().waitForEvent('page');
  await page.getByTestId('confirm-dialog-confirm').click();
  const opened = await popup;
  expect(opened.url()).toBe(new URL(href).href);
  await opened.close();
}

test('Dossier and Wiki frames name the target host before opening an external page', async (
  { page, devBackend }, testInfo,
) => {
  test.setTimeout(180_000);
  // Real Dossiers from this checkout whose page carries an external link.
  const paths = await (await fetch(`${devBackend.baseUrl}/api/watch-paths`)).json() as { name: string }[];
  const candidates: DossierCandidate[] = [];
  for (const candidate of paths) {
    const response = await fetch(
      `${devBackend.baseUrl}/api/projects/${encodeURIComponent(candidate.name)}/workbenches`);
    if (!response.ok) continue;
    const catalogue = await response.json() as { items?: { id: string; entryPath: string; valid: boolean }[] };
    for (const item of catalogue.items ?? []) {
      const file = path.join(REPOSITORY_ROOT, item.entryPath);
      if (item.valid && fs.existsSync(file) && EXTERNAL_LINK.test(fs.readFileSync(file, 'utf8')))
        candidates.push({ projectName: candidate.name, id: item.id, entryPath: item.entryPath });
    }
  }
  expect(candidates.length, 'The real backend must expose a valid Dossier with an external link.')
    .toBeGreaterThan(0);

  await mockSlowBootProbes(page);
  // The external target never leaves the test machine.
  await page.context().route('https://**/*', route => route.fulfill({
    status: 200, contentType: 'text/html', body: '<title>External</title>',
  }));

  const projectName = candidates[0].projectName;
  await page.goto('/', { waitUntil: 'domcontentloaded', timeout: 60_000 });
  await page.addStyleTag({ content: '[data-testid="offline-banner"] { display: none !important; }' });
  const projectRow = page.getByTestId(`studio-explorer-project-${projectName}`);
  await expect(projectRow).toBeVisible();
  if (await projectRow.getAttribute('aria-expanded') === 'false') await projectRow.click();
  const workbenchesRow = page.getByTestId(`studio-explorer-project-workbenches-${projectName}`);
  await expect(workbenchesRow).toBeVisible();
  if (await workbenchesRow.getAttribute('aria-expanded') === 'false') await workbenchesRow.click();
  let dossier: DossierCandidate | null = null;
  for (const candidate of candidates.filter(item => item.projectName === projectName)) {
    const row = page.getByTestId(`studio-explorer-workbench-${projectName}-${candidate.id}`);
    if (await row.isVisible() && await row.isEnabled()) {
      dossier = candidate;
      await row.click();
      break;
    }
  }
  expect(dossier, 'A Dossier with an external link must be listed in the explorer.').not.toBeNull();
  await expect(page.getByTestId('workbench-viewer-frame')).toBeVisible();
  const dossierFrame = page.frameLocator('[data-testid="workbench-viewer-frame"]');
  await confirmExternalLink(page, dossierFrame, evidencePath(testInfo, 'dossier-external-link-dialog.png'));

  await page.goto(`/#/projects/${slugFor(projectName)}/wiki`);
  await expect(page.getByTestId('project-wiki-section')).toBeVisible({ timeout: 15_000 });
  const wikiRelPath = dossier!.entryPath.replace(/^docs\//, '');
  const folders = wikiRelPath.split('/').slice(0, -1);
  for (let depth = 1; depth <= folders.length; depth += 1) {
    const chevron = page.getByTestId(`project-wiki-chevron-${folders.slice(0, depth).join('/')}`);
    if (await chevron.getAttribute('aria-label') === 'Expand') await chevron.click();
  }
  await page.getByTestId(`project-wiki-file-${wikiRelPath}`).click();
  await expect(page.getByTestId('project-wiki-html-frame')).toBeVisible({ timeout: 15_000 });
  const wikiFrame = page.frameLocator('[data-testid="project-wiki-html-frame"]');
  await confirmExternalLink(page, wikiFrame, evidencePath(testInfo, 'wiki-external-link-dialog.png'));
});
