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
    limits: { modelTokensPerWeek: 100, computeCoreHoursPerWeek: 10, hostingSlots: 2, weeklyBudgetUsd: 0.42, includeCacheReads: false },
    usage: { modelTokens: 100, uncachedTokens: 25, modelCostUsd: 0.42, computeCoreHours: 2, hostingSlots: 1, unpricedRuns: 1 },
    remaining: { modelTokens: 75, modelCostUsd: 0, computeCoreHours: 8, hostingSlots: 1 },
    status: 'blocked', meterComplete: true, overrideActive: false, codingAllowed: false, hostingAllowed: false,
    message: 'Reached weekly USD budget 0.42 / 0.42 USD. Resets 2026-10-19 00:00 UTC. New admission paused.',
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
  await page.getByTestId('studio-titlebar-settings').click();
  await page.getByTestId('workspace-settings-rail-workspaces').click();
  // The quota toggle renders once the settings view has read the workspace registry.
  const quotaToggle = page.getByTestId(`settings-workspace-quota-${workspaceId}`);
  await expect(quotaToggle).toBeVisible({ timeout: 30_000 });
  await quotaToggle.click();
  const panel = page.getByTestId('workspace-quota');
  await expect(panel).toBeVisible();
  await expect(panel).toContainText('100 tokens used');
  await expect(panel).toContainText('25 uncached tokens');
  await expect(panel).toContainText('75 left');
  await expect(panel).toContainText('Unpriced usage is outside this budget');
  await panel.getByLabel('Weekly USD budget', { exact: true }).fill('20');
  await expect(panel.getByTestId('workspace-quota-cost')).toContainText('priced subtotal + 1 run without price');
  await expect(panel).toContainText('weekly USD budget');
  await panel.getByLabel('Tokens per week').fill('200');
  await panel.getByRole('button', { name: 'Save quota' }).click();
  await expect.poll(() => saved?.['modelTokensPerWeek']).toBe(200);
  await expect.poll(() => saved?.['weeklyBudgetUsd']).toBe(20);
  expect(saved?.['includeCacheReads']).toBe(false);
  await panel.getByLabel('Override reason').fill('Incident response');
  await panel.getByRole('button', { name: 'Override until week end' }).click();
  await expect.poll(() => overrideReason).toBe('Incident response');
  await panel.getByText('Daily trend and biggest consumers').click();
  await expect(panel).toContainText('AGT-1');
  await expect(panel).toContainText('Mon 12 Oct');
  const results = process.env.JOB_RESULTS_DIR;
  if (results) await panel.screenshot({ path: path.join(results, 'workspace-quota-light--mocked.png') });
  await page.evaluate(() => document.documentElement.setAttribute('data-studio-theme', 'dark'));
  await expect(page.locator('html')).toHaveAttribute('data-studio-theme', 'dark');
  if (results) await page.getByTestId('workspace-quota').screenshot({ path: path.join(results, 'workspace-quota-dark--mocked.png') });
  await page.setViewportSize({ width: 390, height: 844 });
  if (results) await page.screenshot({ path: path.join(results, 'workspace-quota-phone-dark--mocked.png') });
  await panel.getByLabel('Weekly USD budget', { exact: true }).scrollIntoViewIfNeeded();
  if (results) await page.screenshot({ path: path.join(results, 'workspace-quota-phone-editor-dark--mocked.png') });
});

test('a blocked workspace quota shows in the board header and on waiting cards', async ({ page }) => {
  test.setTimeout(90_000);
  const watchPaths = await api<Array<{ path: string }>>('/api/watch-paths');
  expect(watchPaths.length).toBeGreaterThan(0);
  const watchPath = watchPaths[0].path;
  const title = 'Quota waiting card';
  const message = 'Reached weekly token limit 100 / 100 (including cache reads). Resets 2026-10-19 00:00 UTC. New admission paused.';
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
    await expect(card).toBeVisible({ timeout: 60_000 });
    // A cold e2e backend answers the first header reads slowly; the quota read waits behind them.
    await expect(page.getByTestId('workspace-quota-status')).toHaveAttribute('data-status', 'blocked', { timeout: 30_000 });
    await expect(page.getByTestId('workspace-quota-status')).toContainText('weekly token limit');
    await expect(card.getByTestId('task-card-workspace-quota')).toContainText('Admission paused');
    expect(boardReads).toBeGreaterThan(0);
    expect(perProjectReads).toEqual([]);
    const results = process.env.JOB_RESULTS_DIR;
    if (results) await card.screenshot({ path: path.join(results, 'workspace-quota-card--mocked.png') });
    await page.setViewportSize({ width: 1024, height: 900 });
    for (const theme of ['light', 'dark']) {
      await page.evaluate(value => document.documentElement.setAttribute('data-studio-theme', value), theme);
      await expect(page.getByTestId('workspace-quota-status')).toBeVisible();
      if (results) await page.screenshot({ path: path.join(results, `workspace-quota-header-1024-${theme}--mocked.png`) });
    }
  } finally {
    await removeCards();
  }
});

test('project owner can set weekly USD and card budgets', async ({ page }) => {
  test.setTimeout(90_000);
  const paths = await api<Array<{ name: string; path: string }>>('/api/watch-paths');
  const project = paths[0];
  expect(project).toBeTruthy();
  let weeklyUsd = 50;
  let savedCard: { usd: number | null; uncachedTokens: number | null } | null = null;
  await page.route('**/api/workspaces/*/projects/*/quota', async route => {
    const ids = new URL(route.request().url()).pathname.split('/');
    if (route.request().method() === 'PUT') {
      weeklyUsd = (route.request().postDataJSON() as { weeklyBudgetUsd: number }).weeklyBudgetUsd;
      return route.fulfill({ status: 204 });
    }
    return route.fulfill({ json: {
      workspaceId: ids[3], projectId: ids[5], weekStartUtc: '2026-11-16T00:00:00Z', weekEndUtc: '2026-11-23T00:00:00Z',
      limits: { weeklyBudgetUsd: weeklyUsd, modelTokensPerWeek: null, computeCoreHoursPerWeek: null, hostingSlots: null, includeCacheReads: false },
      usage: { modelTokens: 1000, uncachedTokens: 100, modelCostUsd: 12, computeCoreHours: 0, hostingSlots: 0 },
      remaining: { modelTokens: 0, modelCostUsd: weeklyUsd - 12, computeCoreHours: 0, hostingSlots: 0 },
      status: 'ok', meterComplete: true, overrideActive: false, codingAllowed: true, hostingAllowed: true,
      message: null, biggestConsumers: [], biggestProjects: [], dailyTrend: [],
    } });
  });
  await page.route('**/api/workspaces/*/projects/*/quota/cards/BUD-1', async route => {
    if (route.request().method() === 'PUT') {
      savedCard = route.request().postDataJSON() as typeof savedCard;
      return route.fulfill({ status: 204 });
    }
    return route.fulfill({ json: { usd: 5, uncachedTokens: null } });
  });
  const slug = project.name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.goto(`/#/projects/${slug}/settings`);
  const panel = page.getByTestId('project-settings-panel').getByTestId('workspace-quota');
  await expect(panel).toBeVisible({ timeout: 60_000 });
  await expect(panel).toContainText('Project quota');
  await panel.getByLabel('Weekly USD budget', { exact: true }).fill('60');
  await panel.getByRole('button', { name: 'Save quota', exact: true }).click();
  await expect.poll(() => weeklyUsd).toBe(60);
  await expect(panel).toContainText('$48.00 remaining');
  await panel.getByLabel('Card id or key').fill('BUD-1');
  await panel.getByRole('button', { name: 'Load card budget' }).click();
  await expect(panel.getByLabel('Card USD budget')).toHaveValue('5');
  await panel.getByLabel('Card USD budget').fill('10');
  await panel.getByRole('button', { name: 'Save card budget' }).click();
  await expect.poll(() => savedCard).toEqual({ usd: 10, uncachedTokens: null });
  await panel.getByLabel('Card USD budget').fill('');
  await panel.getByLabel('Card tokens excluding cache reads').fill('500');
  await panel.getByRole('button', { name: 'Save card budget' }).click();
  await expect.poll(() => savedCard).toEqual({ usd: null, uncachedTokens: 500 });
  for (const theme of ['light', 'dark']) {
    await page.evaluate(value => document.documentElement.setAttribute('data-studio-theme', value), theme);
    if (process.env.JOB_RESULTS_DIR) await panel.screenshot({ path: path.join(process.env.JOB_RESULTS_DIR, `project-card-budget-${theme}--mocked.png`) });
  }
});
