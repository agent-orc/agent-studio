import { test, expect, Page } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';

/**
 * Visual evidence spec for the lane-container header cleanup: captures
 * the default board and focus-expanded states into the job folder's
 * `results/` directory so they survive the `test-results/` rotation.
 */

const FIXTURE_WATCH = 'C:/fixtures/lane-containers-shots';
const FIXTURE_PROJECT = 'lane-containers-shots';

const JOB_RESULTS = process.env.JOB_RESULTS_DIR
  ?? 'C:/Projects/agent-taskboard-workspace/projects/agent-taskboard/3-progress/remove-useless-lane-collapse-triangles-from-rail-headers/results';

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
    cliType: (over['cliType'] ?? 'claude') as string | null,
    useOwnSession: null,
    lastUsage: null,
    execution: over['execution'] ?? null,
    commit: null,
    pendingIntent: null,
    autoLoop: null,
    summaryState: null,
    ownerClientId: null
  };
}

function fixtureGrouped(): Record<string, unknown[]> {
  return {
    backlog: [
      jobInfo({ id: 'fx-back-1', title: 'Idea: improve quota probe', state: '0-backlog' }),
      jobInfo({ id: 'fx-back-2', title: 'Idea: deeper diff view', state: '0-backlog', order: 2 }),
      jobInfo({ id: 'fx-back-3', title: 'Idea: archive bulk export', state: '0-backlog', order: 3 })
    ],
    preparation: [jobInfo({ id: 'fx-prep-1', title: 'Drafting orchestrator note', state: '1-preparation' })],
    orchestratorPrep: [],
    ready: [
      jobInfo({ id: 'fx-ready-1', title: 'Ready: pickup verification', state: '2-ready' }),
      jobInfo({ id: 'fx-ready-2', title: 'Ready: review banner copy', state: '2-ready', order: 2 })
    ],
    progress: [jobInfo({ id: 'fx-progress-1', title: 'Live: kanban container restructure', state: '3-progress' })],
    failedPickup: [],
    autoReview: [
      jobInfo({ id: 'fx-auto-1', title: 'Auto: orchestrator deciding', state: '4-auto-review' })
    ],
    humanReview: [
      jobInfo({ id: 'fx-human-1', title: 'Awaiting your accept', state: '5-human-review' }),
      jobInfo({ id: 'fx-human-2', title: 'Decision needed: copy', state: '5-human-review', order: 2 })
    ],
    review: [],
    completed: [
      jobInfo({ id: 'fx-done-1', title: 'Wrapped: lane-overlap fix', state: '6-completed' }),
      jobInfo({ id: 'fx-done-2', title: 'Wrapped: search chip', state: '6-completed', order: 2 })
    ],
    archive: [
      jobInfo({ id: 'fx-arch-1', title: 'Archive: alpha stub', state: '7-archive' }),
      jobInfo({ id: 'fx-arch-2', title: 'Archive: alpha stub 2', state: '7-archive', order: 2 }),
      jobInfo({ id: 'fx-arch-3', title: 'Archive: alpha stub 3', state: '7-archive', order: 3 })
    ]
  };
}

async function installBoardMocks(page: Page): Promise<void> {
  const grouped = fixtureGrouped();
  const allJobs = ([] as unknown[]).concat(...Object.values(grouped));

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
  for (const url of [
    '**/api/clients/**',
    '**/api/git/summary',
    '**/api/git/projects',
  ]) {
    await page.route(url, async (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  }
  await page.route('**/api/environment', async (route) =>
    route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify({ devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } }) }));
  await page.route('**/api/cli/quota', async (route) =>
    route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify({ at: '2026-05-05T09:00:00Z', ttlSeconds: 600, snapshots: [] }) }));
  await page.route('**/api/cli/usage', async (route) =>
    route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify({ at: '2026-05-05T09:00:00Z', sections: [] }) }));
  await page.route('**/api/orchestrator/global', async (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ session: null }) }));
  await page.route('**/api/projects/*/settings', async (route) =>
    route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify({ autoCommit: false, runnerMode: 'manual', orchestratorModel: null }) }));
  await page.route('**/api/dev-tools/flags', async (route) =>
    route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify({ updateStableEnabled: false, deleteE2EJobsEnabled: false }) }));
}

test.describe('Lane container visual evidence', () => {
  test.use({ viewport: { width: 1600, height: 900 } });

  test('capture cleaned rail headers into the job results folder', async ({ page }) => {
    fs.mkdirSync(JOB_RESULTS, { recursive: true });
    await installBoardMocks(page);
    await page.goto('/');
    await expect(page.getByTestId('kanban-dashboard')).toBeVisible({ timeout: 10_000 });

    await expect(page.getByTestId('lane-group-toggle-backlog')).toHaveCount(0);
    await expect(page.getByTestId('lane-group-toggle-active')).toHaveCount(0);
    await expect(page.getByTestId('lane-group-toggle-decide')).toHaveCount(0);
    await page.screenshot({ path: path.join(JOB_RESULTS, 'after-rail-header-triangles-removed.png'), fullPage: false });

    // Active focus-expanded.
    await page.getByTestId('lane-group-focus-active').click();
    await expect(page.getByTestId('lane-group-active')).toBeVisible();
    await expect(page.getByTestId('lane-group-backlog')).toBeHidden();
    await expect(page.getByTestId('lane-group-decide')).toBeHidden();
    await page.screenshot({ path: path.join(JOB_RESULTS, 'after-active-focused.png'), fullPage: false });
    await page.getByTestId('lane-group-focus-active').click();

    // Done & Decide focus-expanded.
    await page.getByTestId('lane-group-focus-decide').click();
    await expect(page.getByTestId('lane-group-decide')).toBeVisible();
    await expect(page.getByTestId('lane-group-backlog')).toBeHidden();
    await expect(page.getByTestId('lane-group-active')).toBeHidden();
    await page.screenshot({ path: path.join(JOB_RESULTS, 'after-decide-focused.png'), fullPage: false });
  });
});
