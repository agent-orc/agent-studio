import { test, expect } from '@playwright/test';
import path from 'path';

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
  await page.getByTestId(`settings-workspace-quota-${workspaceId}`).click();
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
  const results = process.env.JOB_RESULTS_DIR;
  if (results) await panel.screenshot({ path: path.join(results, 'workspace-quota.png') });
  await page.evaluate(() => localStorage.setItem('atp.studio.theme', 'dark'));
  await page.reload();
  if (!await page.getByTestId('workspace-settings-rail-workspaces').isVisible()) {
    await page.getByTestId('studio-ab-settings').click();
  }
  await page.getByTestId('workspace-settings-rail-workspaces').click();
  await page.getByTestId(`settings-workspace-quota-${workspaceId}`).click();
  await expect(page.locator('html')).toHaveAttribute('data-studio-theme', 'dark');
  if (results) await page.getByTestId('workspace-quota').screenshot({ path: path.join(results, 'workspace-quota-dark.png') });
});
