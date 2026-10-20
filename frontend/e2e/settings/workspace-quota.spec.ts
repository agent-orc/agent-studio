import { test, expect } from '@playwright/test';
import path from 'path';
import { api } from '../helpers/api';
import { createJob } from '../helpers/jobs';

test('owner can inspect and edit a blocked workspace quota', async ({ page, request }) => {
  const workspaces = await (await request.get('/api/workspaces')).json() as Array<{ id: string }>;
  expect(workspaces.length).toBeGreaterThan(0);
  const workspaceId = workspaces[0].id;
  let saved: Record<string, unknown> | null = null;
  let overrideReason: string | null = null;
  const snapshot = {
    workspaceId, projectId: null, weekStartUtc: '2026-10-12T00:00:00Z', weekEndUtc: '2026-10-19T00:00:00Z',
    limits: { modelTokensPerWeek: 100, computeCoreHoursPerWeek: 10, hostingSlots: 2 },
    usage: { modelTokens: 100, modelCostUsd: 0.42, computeCoreHours: 2, hostingSlots: 1 },
    remaining: { modelTokens: 0, modelCostUsd: 0, computeCoreHours: 8, hostingSlots: 1 },
    status: 'blocked', meterComplete: true, overrideActive: false, codingAllowed: false, hostingAllowed: false,
    message: 'Weekly quota reached. New work is paused until Monday 00:00 UTC or an owner override.',
    biggestConsumers: [{ projectId: 'p1', project: 'Project One', cardId: 'AGT-1', modelTokens: 100, computeCoreHours: 2 }],
    biggestProjects: [{ projectId: 'p1', project: 'Project One', modelTokens: 100, computeCoreHours: 2 }],
    dailyTrend: Array.from({ length: 7 }, (_, index) => ({ modelTokens: index === 0 ? 100 : 0,
      modelCostUsd: index === 0 ? 0.42 : 0, computeCoreHours: index === 0 ? 2 : 0, hostingSlots: 0 })),
  };
  await page.route(`**/api/workspaces/${workspaceId}/quota`, async route => {
    if (route.request().method() === 'PUT') {
      saved = route.request().postDataJSON() as Record<string, unknown>;
      await route.fulfill({ status: 204 });
    } else {
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(snapshot) });
    }
  });
  await page.route(`**/api/workspaces/${workspaceId}/quota/override`, async route => {
    overrideReason = (route.request().postDataJSON() as { reason: string }).reason;
    await route.fulfill({ status: 204 });
  });

  await page.goto('/');
  await page.getByTestId('studio-ab-settings').click();
  await page.getByTestId('workspace-settings-rail-workspaces').click();
  // The quota toggle renders once the settings view has read the workspace registry.
  const quotaToggle = page.getByTestId(`settings-workspace-quota-${workspaceId}`);
  await expect(quotaToggle).toBeVisible({ timeout: 30_000 });
  await quotaToggle.click();
  const panel = page.getByTestId('workspace-quota');
  await expect(panel).toBeVisible();
  await expect(panel).toContainText('100 tokens used');
  await expect(panel).toContainText('Weekly quota reached');
  await panel.getByLabel('Tokens per week').fill('200');
  await panel.getByRole('button', { name: 'Save quota' }).click();
  await expect.poll(() => saved?.['modelTokensPerWeek']).toBe(200);
  await panel.getByLabel('Override reason').fill('Incident response');
  await panel.getByRole('button', { name: 'Override until week end' }).click();
  await expect.poll(() => overrideReason).toBe('Incident response');
  await panel.getByText('Daily trend and biggest consumers').click();
  await expect(panel).toContainText('AGT-1');
  await expect(panel).toContainText('Mon 12 Oct');
  const results = process.env.JOB_RESULTS_DIR;
  if (results) await panel.screenshot({ path: path.join(results, 'workspace-quota--mocked.png') });
  await page.evaluate(() => document.documentElement.setAttribute('data-studio-theme', 'dark'));
  await expect(page.locator('html')).toHaveAttribute('data-studio-theme', 'dark');
  if (results) await page.getByTestId('workspace-quota').screenshot({ path: path.join(results, 'workspace-quota-dark--mocked.png') });
});

test('a blocked workspace quota shows in the board header and on waiting cards', async ({ page }) => {
  const watchPaths = await api<Array<{ path: string }>>('/api/watch-paths');
  expect(watchPaths.length).toBeGreaterThan(0);
  const watchPath = watchPaths[0].path;
  const title = 'Quota waiting card';
  const message = 'Weekly quota reached. New work is paused until Monday 00:00 UTC or an owner override.';
  const scope = (project: string | null, status: string, codingAllowed: boolean, text: string | null) =>
    ({ projectId: project, project, status, message: text, overrideActive: false, codingAllowed, hostingAllowed: codingAllowed, limited: status !== 'ok' });
  // The board reads the workspace and every project admission in one request per poll.
  let boardReads = 0;
  await page.route('**/api/workspaces/*/quota/admission', route => {
    boardReads++;
    const workspaceId = new URL(route.request().url()).pathname.split('/')[3];
    return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({
      workspaceId, workspace: scope(null, 'blocked', false, message), projects: [scope('Another project', 'ok', true, null)] }) });
  });
  const perProjectReads: string[] = [];
  page.on('request', request => { if (/\/api\/workspaces\/[^/]+\/projects\/[^/]+\/quota$/.test(new URL(request.url()).pathname)) perProjectReads.push(request.url()); });
  // The backend assigns the card key, so cleanup matches this spec's cards by title.
  const removeCards = async () => {
    const tasks = await api<Array<{ id: string; title: string; watchPath: string }>>('/api/tasks?includeFixtures=true');
    for (const task of tasks.filter(task => task.title === title && task.watchPath === watchPath))
      await api(`/api/tasks/${encodeURIComponent(task.id)}?watchPath=${encodeURIComponent(watchPath)}`, { method: 'DELETE' });
  };
  await removeCards();
  await createJob({ title, watchPath, targetState: '2-ready', fixture: false });
  try {
    await page.goto('/');
    const card = page.locator('.task-card', { hasText: title });
    await expect(card).toBeVisible({ timeout: 30_000 });
    // A cold e2e backend answers the first header reads slowly; the quota read waits behind them.
    await expect(page.getByTestId('workspace-quota-status')).toHaveAttribute('data-status', 'blocked', { timeout: 30_000 });
    await expect(page.getByTestId('workspace-quota-status')).toContainText('Weekly quota reached');
    await expect(card.getByTestId('task-card-workspace-quota')).toContainText('Weekly quota reached');
    expect(boardReads).toBeGreaterThan(0);
    expect(perProjectReads).toEqual([]);
    const results = process.env.JOB_RESULTS_DIR;
    if (results) await card.screenshot({ path: path.join(results, 'workspace-quota-card--mocked.png') });
  } finally {
    await removeCards();
  }
});
