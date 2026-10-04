import { test, expect, Page } from '@playwright/test';
import { listJobs } from '../helpers/jobs';

/**
 * Activity log - live "agent is working" indicator.
 *
 * The user reported that the bottom of the chat carried no signal of life:
 * once the last visible turn rendered, the agent could spend a minute
 * thinking and the panel looked frozen. The fix adds a small pulsing row
 * below the last turn that names the current activity ("Reading prompt.md",
 * "Searching for foo", "Thinking...") and counts seconds since the last
 * line so the user always sees that the run is alive.
 *
 * This spec stubs the job-detail and output endpoints so the test does not
 * depend on a real running CLI: we synthesise a job in 3-progress with a
 * 'running' execution and a curated output buffer, then assert the live row
 * picks up the verb and target from the latest action.
 */

interface OutLine { timestamp: string; stream: string; text: string; }

function buildRunningJobDetail(jobId: string, watchPath: string) {
  const startedAt = new Date(Date.now() - 12_000).toISOString();
  return {
    info: {
      id: jobId,
      jobKey: `${watchPath}::${jobId}`,
      title: 'Live status spec fixture',
      state: '3-progress',
      agent: 'claude',
      cliType: 'claude',
      model: 'claude-opus-4-7',
      watchPath,
      projectName: 'fixture',
      folderPath: `${watchPath}/.orchestrator/jobs/3-progress/${jobId}`,
      sessionName: '00000000-0000-0000-0000-000000000000',
      lastUsage: null,
      execution: {
        jobId,
        jobKey: `${watchPath}::${jobId}`,
        processId: 1234,
        startedAt,
        status: 'running',
        exitCode: null,
        durationSeconds: null,
        model: 'claude-opus-4-7'
      },
      order: 1
    },
    promptMarkdown: 'Pretend prompt.',
    statusMarkdown: null,
    log: [],
    promptHistory: [],
    summaryState: { status: 'none', startedAt: null, finishedAt: null, errorMessage: null }
  };
}

function buildOutputBuffer(): OutLine[] {
  // Most recent line wins for the live status. Anchor everything ~3 s in
  // the past so the "since" chip lands at "3s" without flakiness.
  const t0 = Date.now() - 8_000;
  const t = (offset: number) => new Date(t0 + offset).toISOString();
  return [
    { timestamp: t(0),    stream: 'stdout', text: 'Looking at the activity-log component to understand the chat surface.' },
    { timestamp: t(1500), stream: 'stdout', text: '* Read prompt.md' },
    { timestamp: t(1600), stream: 'stdout', text: '  | prompt.md' },
    { timestamp: t(3000), stream: 'stdout', text: '* Search "live status"' },
    { timestamp: t(3100), stream: 'stdout', text: '  | searching for matches' },
    { timestamp: t(4500), stream: 'stdout', text: '* Edit src/app/components/activity-log-view.ts' },
    { timestamp: t(4600), stream: 'stdout', text: '  | adding live indicator' }
  ];
}

async function pickAnyJob(): Promise<{ id: string; watchPath: string } | null> {
  // We need any job that exists so the frontend's ?job=&watchPath= deep
  // link survives the click-through; the actual response is mocked.
  const jobs = await listJobs();
  if (jobs.length === 0) return null;
  return { id: jobs[0].id, watchPath: jobs[0].watchPath };
}

async function installRunningJobMocks(page: Page, target: { id: string; watchPath: string }, output: OutLine[]): Promise<void> {
  const detailBody = JSON.stringify(buildRunningJobDetail(target.id, target.watchPath));

  await page.route(`**/api/v1/projects/*/tasks/${encodeURIComponent(target.id)}?**`, async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: detailBody });
  });
  await page.route(`**/api/tasks/${encodeURIComponent(target.id)}/output?**`, async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(output) });
  });
  // Stub run timeline + session events so the activity tab does not throw
  // on missing endpoints under the mock.
  await page.route(`**/api/tasks/${encodeURIComponent(target.id)}/runs?**`, async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ runs: [] }) });
  });
  await page.route(`**/api/tasks/${encodeURIComponent(target.id)}/session-events?**`, async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ events: [], sessionChain: [] }) });
  });
  await page.route(`**/api/tasks/${encodeURIComponent(target.id)}/claude-session?**`, async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(null) });
  });
}

test.describe('Activity log - live status indicator', () => {
  // The live-status row belongs to the LEGACY activity-log-view. With
  // `Frontend:NextGenChat` default-ON the Activity tab would mount the
  // next-gen conversation view instead, so pin the flag off ('0', not
  // key-removal) to keep the legacy host — and its live row — rendered.
  test.beforeEach(async ({ page }) => {
    await page.addInitScript(() => localStorage.setItem('atp.flag.nextGenChat', '0'));
  });

  test('renders a pulsing live row that names the current tool action', async ({ page }) => {
    const target = await pickAnyJob();
    if (!target) {
      test.skip(true, 'No jobs on the board to attach mocks to.');
      return;
    }
    await installRunningJobMocks(page, target, buildOutputBuffer());

    await page.goto(`/?job=${encodeURIComponent(target.id)}&watchPath=${encodeURIComponent(target.watchPath)}`);

    const activityTab = page.getByTestId('inspector-tab-activity');
    await expect(activityTab).toBeVisible({ timeout: 10_000 });
    await activityTab.click();

    const live = page.getByTestId('activity-log-live-status');
    await expect(live).toBeVisible({ timeout: 5_000 });

    // The latest action in the buffer is an Edit, so the verb must reflect that.
    const verb = page.getByTestId('activity-log-live-verb');
    await expect(verb).toHaveText(/Editing/i);

    const detail = page.getByTestId('activity-log-live-detail');
    await expect(detail).toBeVisible();
    await expect(detail).toContainText('activity-log-view.ts');

    // The "since last line" chip must show a small elapsed value (>= 1s).
    const since = page.getByTestId('activity-log-live-since');
    await expect(since).toBeVisible();
    await expect(since).toHaveText(/^\d+s$/);

    // The kind attribute drives the colour scheme - assert the data-kind
    // is wired so future colour changes do not silently regress.
    await expect(live).toHaveAttribute('data-kind', 'tool');

    // Capture a screenshot so the reviewer can see the indicator in
    // context. The activity-log-body is a scroll container; scroll the
    // live row into view, then screenshot the protocol pane so the
    // indicator lands in the frame together with the last conversation
    // turn rather than just the row itself in isolation.
    await page.setViewportSize({ width: 1400, height: 1000 });
    await live.scrollIntoViewIfNeeded();
    await page.waitForTimeout(150);
    const pane = page.getByTestId('pane-protocol');
    await pane.screenshot({ path: 'activity-log-live-status.png' });
  });

  test('falls back to "Thinking" when the latest activity is free-form agent text', async ({ page }) => {
    const target = await pickAnyJob();
    if (!target) {
      test.skip(true, 'No jobs on the board to attach mocks to.');
      return;
    }
    const t0 = Date.now() - 4_000;
    const output: OutLine[] = [
      { timestamp: new Date(t0).toISOString(), stream: 'stdout',
        text: 'Considering how to phrase the live indicator label.' }
    ];
    await installRunningJobMocks(page, target, output);

    await page.goto(`/?job=${encodeURIComponent(target.id)}&watchPath=${encodeURIComponent(target.watchPath)}`);
    await page.getByTestId('inspector-tab-activity').click();

    const live = page.getByTestId('activity-log-live-status');
    await expect(live).toBeVisible({ timeout: 5_000 });
    await expect(page.getByTestId('activity-log-live-verb')).toHaveText('Thinking');
    await expect(live).toHaveAttribute('data-kind', 'agent');
  });

  test('does not render when the run is not active', async ({ page }) => {
    const target = await pickAnyJob();
    if (!target) {
      test.skip(true, 'No jobs on the board to attach mocks to.');
      return;
    }
    // Re-use the running fixture but flip status to completed.
    const detail = buildRunningJobDetail(target.id, target.watchPath);
    detail.info.execution!.status = 'completed';
    detail.info.execution!.exitCode = 0;
    detail.info.execution!.durationSeconds = 12;

    await page.route(`**/api/v1/projects/*/tasks/${encodeURIComponent(target.id)}?**`, async (route) => {
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(detail) });
    });
    await page.route(`**/api/tasks/${encodeURIComponent(target.id)}/output?**`, async (route) => {
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(buildOutputBuffer()) });
    });
    await page.route(`**/api/tasks/${encodeURIComponent(target.id)}/runs?**`, async (route) => {
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ runs: [] }) });
    });
    await page.route(`**/api/tasks/${encodeURIComponent(target.id)}/session-events?**`, async (route) => {
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ events: [], sessionChain: [] }) });
    });
    await page.route(`**/api/tasks/${encodeURIComponent(target.id)}/claude-session?**`, async (route) => {
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(null) });
    });

    await page.goto(`/?job=${encodeURIComponent(target.id)}&watchPath=${encodeURIComponent(target.watchPath)}`);
    await page.getByTestId('inspector-tab-activity').click();

    // Wait for the activity log to mount, then assert no live row exists.
    await expect(page.getByTestId('activity-log-body')).toBeVisible({ timeout: 5_000 });
    await expect(page.getByTestId('activity-log-live-status')).toHaveCount(0);
  });
});
