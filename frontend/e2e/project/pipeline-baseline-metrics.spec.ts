import { test, expect } from '../fixtures/dev-backend';
import * as fs from 'fs';
import * as path from 'path';
import { setTheme } from '../helpers/theme';

/**
 * AGT-3309 evidence: the delivery-target baseline rows of the Ops "Gate
 * economics" block, rendered from the REAL backend projection
 * (`GET /api/gate-economics`). Nothing under `/api/**` is mocked. The spec
 * seeds one integrated card the way the pipeline writes it: lane events, a
 * remote pre-develop gate receipt with its source bundle transfer, and a
 * Remote Review settlement journal. It then checks the computed baseline and
 * that the all-projects row equals the sum of the visible project rows.
 */

const RUN_ID = `run-${process.pid}-${Math.random().toString(36).slice(2, 8)}`;
const SCREENSHOT_DIR = (() => {
  const fromEnv = process.env.JOB_RESULTS_DIR;
  if (fromEnv && fromEnv.trim()) return fromEnv;
  return path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'pipeline-baseline-metrics');
})();

const SKIPPED_DIRS = new Set(['checkout', '.git', 'node_modules']);

/** Fallback lookup: the job folder whose own task.json carries the title. */
function findTaskFolder(root: string, title: string, depth = 6): string | null {
  if (depth < 0 || !root || !fs.existsSync(root)) return null;
  const taskJson = path.join(root, 'task.json');
  if (fs.existsSync(taskJson)) {
    try {
      if (JSON.parse(fs.readFileSync(taskJson, 'utf8')).title === title) return root;
    } catch (error) {
      console.warn(`Skipping unreadable task header ${taskJson}: ${String(error)}`);
    }
  }
  for (const entry of fs.readdirSync(root, { withFileTypes: true })) {
    if (!entry.isDirectory() || SKIPPED_DIRS.has(entry.name)) continue;
    const nested = findTaskFolder(path.join(root, entry.name), title, depth - 1);
    if (nested) return nested;
  }
  return null;
}

// Seeding is not idempotent: a retry would count the card twice.
test.describe.configure({ retries: 0 });

test('gate economics renders the delivery-target baseline in both themes', async ({ page, devBackend }) => {
  test.setTimeout(180_000);
  fs.mkdirSync(SCREENSHOT_DIR, { recursive: true });
  const watchPaths = await (await page.request.get(`${devBackend.baseUrl}/api/watch-paths`)).json();
  const watchPath = watchPaths[0].path as string;
  const project = watchPaths[0].name as string;
  const before = await (await page.request.get(`${devBackend.baseUrl}/api/gate-economics?window=7d`)).json();
  const beforeRow = before.rows.find((item: { project: string }) => item.project === project);

  const title = `Pipeline baseline ${RUN_ID}`;
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

  const now = Date.now();
  const at = (minutesAgo: number) => new Date(now - minutesAgo * 60_000).toISOString();
  const sha = 'c'.repeat(40);
  const lane = (kind: string, minutesAgo: number, extra: Record<string, unknown>) => JSON.stringify({
    Project: project, JobId: id, Sequence: 1, AtUtc: at(minutesAgo), Kind: kind, ...extra,
  });
  // Review passed 50 minutes ago; the integrated push landed 10 minutes ago.
  fs.writeFileSync(path.join(folder!, 'integration-lane-events.jsonl'), [
    lane('enqueued', 49, { DeliveredAtUtc: at(50) }),
    lane('published', 10, { Receipt: sha }),
  ].join('\n') + '\n');
  fs.mkdirSync(path.join(folder!, 'post-steps'), { recursive: true });
  fs.writeFileSync(path.join(folder!, 'post-steps', 'pre-develop-build-gate-1.log'),
    'verdict=Ok exit=0 durationMs=600000 failureKind=None\n' +
    `expectedSha=${sha} testedSha=${sha}\n` +
    'reason=verify gate passed (project.yml); test-level=full Source bundle 52428800 bytes transferred in 12,5s.\n' +
    `verdictSource=Executed originalRunId=${RUN_ID} originalCompletedAtUtc=${at(20)}\n` +
    `gateRung=full\ntestLevel=full\nrecordedAtUtc=${at(20)}\n` +
    'gateQueueWaitMs=1800000 mergeLockWaitMs=0 gateCollisionDetected=false\n--- last-300-lines ---\n');
  const started = now - 45 * 60_000;
  const command = (stepId: string, minutes: number, executionKind = 'tool') => ({
    stepId, phase: 'verification', workspaceRole: 'candidate', executionKind,
    startedAt: new Date(started).toISOString(), finishedAt: new Date(started + minutes * 60_000).toISOString(),
  });
  fs.mkdirSync(path.join(folder!, 'logs'), { recursive: true });
  fs.writeFileSync(path.join(folder!, 'logs', `remote-review-settlement-review_${RUN_ID}.json`), JSON.stringify({
    version: 1, attemptId: `review_${RUN_ID}`, receivedAtUtc: at(40),
    report: { commands: [command('verify-1', 3), command('verify-2', 1), command('aspect-code-quality', 6, 'agent-aspect')] },
  }));

  // The projection caches each window for 30 seconds.
  await expect.poll(async () => {
    const projection = await (await page.request.get(`${devBackend.baseUrl}/api/gate-economics?window=7d`)).json();
    return projection.rows.find((item: { project: string }) => item.project === project)?.baseline?.leadTimeCards ?? 0;
  }, { timeout: 60_000, intervals: [2_000, 5_000] }).toBeGreaterThan(beforeRow?.baseline?.leadTimeCards ?? 0);
  const projection = await (await page.request.get(`${devBackend.baseUrl}/api/gate-economics?window=7d`)).json();
  const baseline = projection.rows.find((item: { project: string }) => item.project === project).baseline;
  // T1 3 + 1 minutes (the agent aspect is not test time) plus T2 10 minutes, queue wait excluded.
  expect(baseline.gateMinutesCards).toBeGreaterThanOrEqual(1);
  expect(baseline.fullSuiteRuns).toBeGreaterThanOrEqual(1);
  expect(baseline.gateSourceRuns).toBeGreaterThanOrEqual(1);
  expect(baseline.redDevelopMedianMinutes).toBeNull();
  if (!beforeRow?.baseline?.leadTimeCards) {
    expect(baseline.leadTimeMedianMinutes).toBeCloseTo(40, 0);
    expect(baseline.gateMinutesPerCardMedian).toBeCloseTo(14, 0);
    expect(baseline.gateSourceBytesMedian).toBe(52_428_800);
    expect(baseline.gateSourceSecondsMedian).toBe(12.5);
  }
  expect(projection.boardRead.map((handler: { label: string }) => handler.label))
    .toEqual(['tasks/grouped', 'tasks/list', 'board/integration-status', 'board/merge-status']);
  fs.writeFileSync(path.join(SCREENSHOT_DIR, 'pipeline-baseline-metrics.json'), JSON.stringify(projection, null, 2) + '\n');

  await page.setViewportSize({ width: 2560, height: 1400 });
  await page.goto('/', { waitUntil: 'domcontentloaded', timeout: 30_000 });
  await page.evaluate(() => {
    localStorage.removeItem('atp.studio.tabs.v1');
    localStorage.removeItem('atp.studio.explorer.expanded');
  });
  await page.reload({ waitUntil: 'domcontentloaded', timeout: 30_000 });
  await expect(page.getByTestId('studio-sidebar')).toBeVisible({ timeout: 30_000 });
  const hubRow = page.getByTestId(`studio-explorer-project-deploy-${project}`);
  if (!(await hubRow.count()) || !(await hubRow.isVisible())) {
    await page.getByTestId(`studio-explorer-project-${project}`).first().click();
  }
  await hubRow.click();
  await expect(page.getByTestId('project-deploy-area')).toBeVisible({ timeout: 30_000 });
  await page.getByTestId('deploy-tab-steps').click();
  const economics = page.getByTestId('gate-economics');
  await expect(economics).toBeVisible({ timeout: 30_000 });
  const table = economics.getByTestId('pipeline-baseline-metrics');
  const projectRow = table.locator(`[data-testid="pipeline-baseline-project"][data-project="${project}"]`);
  await expect(projectRow.getByTestId('baseline-source-runs')).not.toHaveText('0', { timeout: 30_000 });
  await expect(projectRow).toContainText('Not measured');

  // The aggregate row is the sum of the visible project rows.
  const sum = async (testId: string) => (await table.getByTestId('pipeline-baseline-project')
    .getByTestId(testId).allTextContents())
    .map(text => Number(text.trim().replace(/,/g, ''))).filter(value => !Number.isNaN(value))
    .reduce((total, value) => total + value, 0);
  const all = table.getByTestId('pipeline-baseline-all');
  await expect(all.getByTestId('baseline-full-suite-runs')).toHaveText(String(await sum('baseline-full-suite-runs')));
  await expect(all.getByTestId('baseline-source-runs')).toHaveText(String(await sum('baseline-source-runs')));
  await expect(economics.getByTestId('board-read-metrics').locator('tbody tr')).toHaveCount(4);

  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await table.scrollIntoViewIfNeeded();
    await economics.screenshot({ path: path.join(SCREENSHOT_DIR, `pipeline-baseline-metrics-${theme}.png`) });
  }
});
