import { test, expect } from '../fixtures/dev-backend';
import * as fs from 'fs';
import * as path from 'path';
import { setTheme } from '../helpers/theme';

/**
 * AGT-3070 evidence: the Ops "Gate economics" block and the pipeline-health
 * row rendered from the REAL backend projection (`GET /api/gate-economics`)
 * over the eight real Windows gate receipts of 28/29 September 2026 (merges
 * 0e2b0f8d to 2272ce8c). Nothing under `/api/**` is mocked.
 *
 * The fixtures in `e2e/fixtures/gate-economics-receipts/` keep each receipt's
 * header lines and `test-selection.json` audit byte for byte; only the long
 * test-output dump in `reason=` is shortened to the failing command, the
 * failing tests and the totals. The receipts predate the new header fields,
 * so queue wait and reuse rung must render as Unknown, never as zero. Their
 * file time is restamped into the 7-day window because the window is read
 * from the file time and the originals are older than seven days.
 */

const RUN_ID = `run-${process.pid}-${Math.random().toString(36).slice(2, 8)}`;
const FIXTURE_DIR = path.resolve(__dirname, '..', 'fixtures', 'gate-economics-receipts');
const SCREENSHOT_DIR = (() => {
  const fromEnv = process.env.JOB_RESULTS_DIR;
  if (fromEnv && fromEnv.trim()) return fromEnv;
  return path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'gate-economics-real-receipts');
})();

const SKIPPED_DIRS = new Set(['checkout', '.git', 'node_modules']);

/** Fallback lookup: the job folder whose own task.json carries the title. */
function findTaskFolder(root: string, title: string, depth = 6): string | null {
  if (depth < 0 || !root || !fs.existsSync(root)) return null;
  const taskJson = path.join(root, 'task.json');
  if (fs.existsSync(taskJson)) {
    try {
      if (JSON.parse(fs.readFileSync(taskJson, 'utf8')).title === title) return root;
    } catch { /* not a readable task header */ }
  }
  for (const entry of fs.readdirSync(root, { withFileTypes: true })) {
    if (!entry.isDirectory() || SKIPPED_DIRS.has(entry.name)) continue;
    const nested = findTaskFolder(path.join(root, entry.name), title, depth - 1);
    if (nested) return nested;
  }
  return null;
}

// Seeding is not idempotent: a retry would count each receipt twice.
test.describe.configure({ retries: 0 });

test('gate economics renders the real 28/29 September receipts in both themes', async ({ page, devBackend }) => {
  test.setTimeout(180_000);
  fs.mkdirSync(SCREENSHOT_DIR, { recursive: true });
  const watchPaths = await (await page.request.get(`${devBackend.baseUrl}/api/watch-paths`)).json();
  const watchPath = watchPaths[0].path as string;
  const project = watchPaths[0].name as string;
  const receipts = fs.readdirSync(FIXTURE_DIR).filter(name => name.endsWith('.log')).sort();
  expect(receipts).toHaveLength(8);

  for (const receipt of receipts) {
    const card = receipt.replace(/\.log$/, '');
    const title = `Gate receipt ${card} ${RUN_ID}`;
    const created = await page.request.post(`${devBackend.baseUrl}/api/tasks`, {
      headers: { 'X-Client-Id': 'local-default' },
      data: { title, watchPath, agent: 'codex', cliType: 'codex', targetState: '1-preparation', fixture: false },
    });
    expect(created.ok(), await created.text()).toBe(true);
    const id = (await created.json()).id as string;
    const detail = await (await page.request.get(
      `${devBackend.baseUrl}/api/tasks/${id}?watchPath=${encodeURIComponent(watchPath)}`)).json();
    const folder = (detail?.info?.folderPath as string | undefined)
      ?? [watchPath, process.env.PW_E2E_WORKSPACE ?? '']
        .map(root => findTaskFolder(root, title)).find(found => found) ?? null;
    expect(folder, `task folder for ${id}`).not.toBeNull();
    const target = path.join(folder!, 'post-steps', 'pre-develop-build-gate-1.log');
    fs.mkdirSync(path.dirname(target), { recursive: true });
    fs.copyFileSync(path.join(FIXTURE_DIR, receipt), target);
  }

  // Hand count over the fixture durations (ms), sorted:
  // 3001541 3012606 3016313 3055919 3090341 3143131 3186026 3348733.
  // Median (3055919 + 3090341) / 2 = 3073130 ms = 51.2 min.
  // p90 at rank 6.3 = 3186026 + 0.3 * 162707 = 3234838 ms = 53.9 min.
  const projection = await (await page.request.get(`${devBackend.baseUrl}/api/gate-economics?window=7d`)).json();
  const row = projection.rows.find((item: { project: string }) => item.project === project);
  expect(row.gatedMerges).toBe(8);
  expect(row.gateRed).toBe(8);
  expect(row.gateMinutesMedian).toBeCloseTo(3073130 / 60000, 6);
  expect(row.gateMinutesP90).toBeCloseTo(3234838.1 / 60000, 6);
  expect(row.queueWaitP90Ms).toBeNull();
  expect(row.exactReuseRate).toBeNull();
  expect(row.fullSuiteShare).toBe(1);
  fs.writeFileSync(path.join(SCREENSHOT_DIR, 'gate-economics-real-receipts.json'), JSON.stringify(projection, null, 2) + '\n');

  await page.setViewportSize({ width: 1920, height: 1080 });
  await page.goto('/', { waitUntil: 'domcontentloaded', timeout: 30_000 });
  await page.evaluate(() => {
    try { localStorage.removeItem('atp.studio.tabs.v1'); } catch { /* ignore */ }
    try { localStorage.removeItem('atp.studio.explorer.expanded'); } catch { /* ignore */ }
  });
  await page.reload({ waitUntil: 'domcontentloaded', timeout: 30_000 });
  await expect(page.getByTestId('studio-sidebar')).toBeVisible({ timeout: 30_000 });
  const hubRow = page.getByTestId(`studio-explorer-project-hub-${project}`);
  if (!(await hubRow.count()) || !(await hubRow.isVisible())) {
    await page.getByTestId(`studio-explorer-project-${project}`).first().click();
  }
  await hubRow.click();
  await expect(page.getByTestId('project-shell')).toBeVisible({ timeout: 30_000 });
  await page.getByTestId('project-shell-rail-pipeline').click();

  const economics = page.getByTestId('gate-economics');
  const projectRow = economics.locator(`[data-testid="gate-economics-project"][data-project="${project}"]`);
  await expect(projectRow.locator('td').nth(0)).toHaveText('8', { timeout: 30_000 });
  await expect(projectRow.locator('td').nth(1)).toHaveText('51.2 min / 53.9 min');
  await expect(projectRow.locator('td').nth(2)).toHaveText('Unknown');
  const counts = await economics.getByTestId('gate-economics-project').locator('td:nth-child(2)').allTextContents();
  await expect(economics.getByTestId('gate-economics-all').locator('td:nth-child(2)'))
    .toHaveText(String(counts.reduce((sum, count) => sum + Number(count.trim()), 0)));
  const healthRow = page.getByTestId('pipeline-health-economics');
  await expect(healthRow).toContainText('8 gated merges · median 51.2 min · queue p90 Unknown');

  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await economics.screenshot({ path: path.join(SCREENSHOT_DIR, `gate-economics-real-receipts-${theme}.png`) });
    await page.getByTestId('pipeline-health').screenshot({ path: path.join(SCREENSHOT_DIR, `pipeline-health-real-receipts-${theme}.png`) });
  }
});
