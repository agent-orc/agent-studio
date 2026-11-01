import { expect, test } from '../fixtures/dev-backend';
import type { Page, TestInfo } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';

test.use({ serviceWorkers: 'block' });

function evidencePath(testInfo: TestInfo, fileName: string): string {
  const jobResultsDir = process.env['JOB_RESULTS_DIR']?.trim();
  if (!jobResultsDir) return testInfo.outputPath(fileName);
  const resultsDir = path.resolve(jobResultsDir);
  fs.mkdirSync(resultsDir, { recursive: true });
  return path.join(resultsDir, fileName);
}

/**
 * Keeps this spec fast and deterministic by short-circuiting app-wide boot
 * probes unrelated to the Dossier surface (model discovery, quota, crash
 * recovery), the same subset `workbench-readonly.spec.ts` mocks out.
 */
async function mockSlowBootProbes(page: Page): Promise<void> {
  await page.route('**/healthz', route => route.fulfill({ status: 200, body: 'Healthy' }));
  await page.route(/\/api\/cli\/[^/]+\/models$/, route => route.fulfill({
    status: 200, contentType: 'application/json', body: JSON.stringify({ models: [], source: 'dossier-assets-e2e' }),
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
 * Regression coverage for the dossier/Wiki viewer 404ing on `<img>` tags that
 * reference a sibling `assets/` subfolder (e.g. `assets/foo.png`). Exercises
 * the real repository fixture at `docs/operations/task-and-board-ui/`
 * (Dossier `task-and-board-ui`, which carries the AGT-2577 screenshots) through the real
 * Workbench viewer sandboxed iframe, over the real backend asset endpoint -
 * no mocked image bytes, so a regression here reproduces the reported broken
 * image the same way the operator saw it.
 */
test('the task-and-board-ui Dossier renders its assets/ screenshots instead of a broken image', async (
  { page, devBackend }, testInfo,
) => {
  test.setTimeout(120_000);
  const pathsResponse = await fetch(`${devBackend.baseUrl}/api/watch-paths`);
  expect(pathsResponse.ok).toBe(true);
  const paths = await pathsResponse.json() as { name: string }[];
  let projectName: string | null = null;
  for (const candidate of paths) {
    const response = await fetch(
      `${devBackend.baseUrl}/api/projects/${encodeURIComponent(candidate.name)}/workbenches?history=true`,
    );
    if (!response.ok) continue;
    const catalogue = await response.json() as { items?: { id: string }[] };
    if (catalogue.items?.some(item => item.id === 'task-and-board-ui')) {
      projectName = candidate.name;
      break;
    }
  }
  expect(projectName, 'The real backend must expose the task-and-board-ui Dossier.').not.toBeNull();

  await mockSlowBootProbes(page);
  await page.goto('/', { waitUntil: 'domcontentloaded', timeout: 60_000 });
  await page.addStyleTag({ content: '[data-testid="offline-banner"] { display: none !important; }' });

  const projectRow = page.getByTestId(`studio-explorer-project-${projectName}`);
  await expect(projectRow).toBeVisible();
  if (await projectRow.getAttribute('aria-expanded') === 'false') await projectRow.click();
  const workbenchesRow = page.getByTestId(`studio-explorer-project-workbenches-${projectName}`);
  await expect(workbenchesRow).toBeVisible();
  await workbenchesRow.click();
  await page.getByTestId(`workbench-overview-item-${projectName}-task-and-board-ui`)
    .getByRole('button', { name: 'Open full view' }).click();

  const frame = page.getByTestId('workbench-viewer-frame');
  await expect(frame).toBeVisible();
  const articleFrame = page.frameLocator('[data-testid="workbench-viewer-frame"]');
  const screenshots = articleFrame.locator('img[src*="task-timeline"]');
  await expect(screenshots.first()).toBeVisible({ timeout: 15_000 });
  await screenshots.first().scrollIntoViewIfNeeded();
  await page.getByTestId('studio-editor').screenshot({
    path: evidencePath(testInfo, 'task-and-board-ui-dossier-assets--after--real.png'),
  });

  const naturalWidths = await screenshots.evaluateAll(
    images => images.map(image => (image as HTMLImageElement).naturalWidth),
  );
  expect(naturalWidths.length).toBeGreaterThan(0);
  for (const width of naturalWidths) expect(width).toBeGreaterThan(0);
});
