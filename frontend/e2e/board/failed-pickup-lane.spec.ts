import { test, expect, Page } from '@playwright/test';

/**
 * ADR-0051 (eliminate the failed-pickup lane) regression spec. Supersedes the
 * ADR-0028 doctrine of a visible dead-letter lane + persistent banner.
 *
 * The board no longer renders a `3a-failed-pickup` lane, banner, toast, or
 * amber dot under any circumstances. No live path populates the lane, and the
 * boot drain empties any historical folders. This spec pins the UI invariant:
 * even if a grouped payload still carries `failedPickup` cards (a drain-era
 * payload arriving before the backend has finished draining), the board shows
 * no lane, no banner, and no dot for them.
 */

const FIXTURE_WATCH = 'C:/fixtures/failed-pickup-demo';
const FIXTURE_PROJECT = 'failed-pickup-demo';

function jobInfo(over: Partial<Record<string, unknown>>): Record<string, unknown> {
  const id = String(over['id'] ?? 'fx-job');
  const state = String(over['state'] ?? '2-ready');
  return {
    id,
    jobKey: `${FIXTURE_WATCH}::${id}`,
    title: String(over['title'] ?? id),
    state,
    order: Number(over['order'] ?? 1),
    agent: String(over['agent'] ?? 'claude'),
    createdAt: '2026-05-05T08:00:00Z',
    watchPath: FIXTURE_WATCH,
    projectName: FIXTURE_PROJECT,
    folderPath: `${FIXTURE_WATCH}/.orchestrator/jobs/${state}/${id}`,
    lastActivity: '2026-05-05T09:00:00Z',
    sessionName: null,
    model: 'claude-opus-4-7',
    cliType: 'claude',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    pendingIntent: null,
    autoLoop: null,
    summaryState: null,
    ownerClientId: null
  };
}

function fixtureGrouped(failedCount: number): Record<string, unknown[]> {
  const failedPickup: unknown[] = [];
  for (let i = 0; i < failedCount; i++) {
    failedPickup.push(jobInfo({
      id: `fx-failed-${i + 1}-orphan-2026-05-06`,
      title: `Pickup failure #${i + 1}`,
      state: '3a-failed-pickup'
    }));
  }
  return {
    preparation: [jobInfo({ id: 'fx-prep-1', title: 'Drafting', state: '1-preparation' })],
    orchestratorPrep: [],
    ready: [jobInfo({ id: 'fx-ready-1', title: 'Ready', state: '2-ready' })],
    progress: [jobInfo({ id: 'fx-progress-1', title: 'Live', state: '3-progress' })],
    failedPickup,
    autoReview: [],
    humanReview: [],
    review: [],
    completed: [jobInfo({ id: 'fx-done-1', title: 'Done', state: '6-completed' })],
    archive: []
  };
}

async function installBoardMocks(page: Page, failedCount: number): Promise<void> {
  const grouped = fixtureGrouped(failedCount);
  const allJobs = [
    ...(grouped.preparation as unknown[]),
    ...(grouped.ready as unknown[]),
    ...(grouped.progress as unknown[]),
    ...(grouped.failedPickup as unknown[]),
    ...(grouped.completed as unknown[])
  ];

  await page.route('**/api/**', async (route) => {
    if (route.request().method() === 'GET') {
      await route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
      return;
    }
    await route.fallback();
  });
  await page.route('**/api/watch-paths', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify([{ name: FIXTURE_PROJECT, path: FIXTURE_WATCH, rootPath: FIXTURE_WATCH }]) });
  });
  await page.route('**/api/tasks', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(allJobs) });
  });
  await page.route('**/api/v1/studio/board', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(grouped) });
  });
  await page.route('**/api/v1/studio/runner/status', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify({ projects: { [FIXTURE_PROJECT]: { projectName: FIXTURE_PROJECT, mode: 'manual', activeJobId: null, activeExecution: null, queuedJobIds: [] } } }) });
  });
  await page.route('**/api/clients/**', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
  });
  await page.route('**/api/environment', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify({ devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } }) });
  });
  await page.route('**/api/git/summary', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
  });
  await page.route('**/api/cli/quota', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify({ at: '2026-05-06T09:00:00Z', ttlSeconds: 600, snapshots: [] }) });
  });
  await page.route('**/api/cli/usage', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify({ at: '2026-05-06T09:00:00Z', sections: [] }) });
  });
  await page.route('**/api/git/projects', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
  });
  await page.route('**/api/orchestrator/global', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ session: null }) });
  });
  await page.route('**/api/projects/*/settings', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify({ autoCommit: false, runnerMode: 'manual', orchestratorModel: null }) });
  });
  await page.route('**/api/dev-tools/flags', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify({ updateStableEnabled: false, deleteE2EJobsEnabled: false }) });
  });
}

test.describe('ADR-0051 failed-pickup lane is eliminated', () => {
  test.use({ viewport: { width: 1440, height: 900 } });

  test('empty failed-pickup group -> no lane, no banner, no dot', async ({ page }) => {
    await installBoardMocks(page, 0);
    await page.goto('/');

    await expect(page.getByTestId('studio-board')).toBeVisible({ timeout: 10_000 });
    await expect(page.getByTestId('failed-pickup-banner')).toHaveCount(0);
    await expect(page.getByTestId('lane-3a-failed-pickup')).toHaveCount(0);
    await expect(page.getByTestId('failed-pickup-dot')).toHaveCount(0);
  });

  test('drain-era payload with failed-pickup cards still renders no lane, banner, or dot', async ({ page }) => {
    await installBoardMocks(page, 2);
    await page.goto('/');

    await expect(page.getByTestId('studio-board')).toBeVisible({ timeout: 10_000 });

    // The lane, the retired banner, and the amber dot must never render, even
    // when the grouped payload still carries failed-pickup cards.
    await expect(page.getByTestId('lane-3a-failed-pickup')).toHaveCount(0);
    await expect(page.getByTestId('failed-pickup-banner')).toHaveCount(0);
    await expect(page.getByTestId('failed-pickup-banner-count')).toHaveCount(0);
    await expect(page.getByTestId('failed-pickup-dot')).toHaveCount(0);

    // No toast offering to open the retired lane either.
    await expect(page.getByTestId('toast-failed-pickup-open-lane')).toHaveCount(0);

    // The lanes that flank the retired position still render, so the board is
    // healthy - the failed-pickup lane simply does not exist between them. The
    // Active container's lane vocabulary is exactly progress -> auto-review.
    await expect(page.getByTestId('lane-3-progress')).toBeVisible();
    await expect(page.getByTestId('lane-4-auto-review')).toBeVisible();
    await expect(page.getByTestId('lane-group-active')).toHaveAttribute(
      'data-states',
      '3-progress,4-auto-review',
    );

    await page.screenshot({ path: 'test-results/failed-pickup-lane-eliminated.png', fullPage: false });
  });
});
