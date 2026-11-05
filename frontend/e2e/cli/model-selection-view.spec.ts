import { test, expect, type Page, type TestInfo } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { api } from '../helpers/api';
import { createJob } from '../helpers/jobs';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

const receipt = {
  id: 'selection-demo-1', at: new Date().toISOString(), kind: 'switched', consumer: 'coding',
  project: 'agent-studio-worktree', taskKey: 'DEMO-1', rule: 'quota-cap', reason: 'Weekly cap reached',
  from: { cliType: 'claude', model: 'claude-opus-5', thinkingLevel: 'high' },
  to: { cliType: 'codex', model: 'gpt-6-sol', thinkingLevel: 'high' },
  quota: { provider: 'claude', window: 'Weekly', usedPct: 98, capPct: 95, projectedPct: 99,
    resetAt: new Date(Date.now() + 86400000).toISOString(), snapshotAgeSeconds: 10 },
  evidence: { excerpt: 'Quota snapshot exceeded the cap', classifierKind: 'quota' },
  returnToPrimaryAt: new Date(Date.now() + 86400000).toISOString(),
};

async function mockSelection(page: Page): Promise<void> {
  await page.route('**/api/model-selection/active', route => route.fulfill({ json: [receipt] }));
  await page.route('**/api/model-selection/effective*', route => route.fulfill({ json: { routes: [{
    consumer: 'coding', stepId: null, configured: receipt.from, effective: receipt.to,
    rule: 'quota-cap', reason: 'Weekly cap reached',
  }] } }));
  await page.route('**/api/model-selection/events*', route => {
    const params = new URL(route.request().url()).searchParams;
    const matches = (!params.get('taskKey') || params.get('taskKey') === receipt.taskKey)
      && (!params.get('eventId') || params.get('eventId') === receipt.id);
    return route.fulfill({ json: { events: matches ? [receipt] : [], nextCursor: null } });
  });
}

function resultPath(testInfo: TestInfo, name: string): string {
  const root = process.env['JOB_RESULTS_DIR'] ?? testInfo.outputDir;
  mkdirSync(root, { recursive: true });
  return join(root, name);
}

test('model selection view and timeline link work in light and dark', async ({ page }, testInfo) => {
  test.setTimeout(120_000);
  const paths = await api<{ path: string }[]>('/api/watch-paths');
  const watchPath = paths[0]?.path;
  if (!watchPath) throw new Error('No watch path configured');
  const { id } = await createJob({ title: `model-selection-view-${Date.now()}`, watchPath,
    promptMarkdown: '# Model selection timeline link', targetState: '0-backlog' });
  receipt.taskKey = id;
  const errors: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  await mockSelection(page);
  await page.route(/\/api\/tasks\/[^/]+\/timeline(\?|$)/, route => route.fulfill({ json: [{
    ts: new Date().toISOString(), kind: 'quota_fallback_activated', actor: 'system',
    summary: 'Fallback selected', details: { selectionEventId: receipt.id, taskKey: id },
  }] }));
  try {
    await page.setViewportSize({ width: 1440, height: 1000 });
    await page.goto('/#/workspace/settings/caps');
    await dismissDevErrorDialog(page);
    const view = page.getByTestId('model-selection-view');
    await expect(view).toBeVisible();
    await expect(view.getByTestId('model-selection-active')).toContainText('1 affected card');
    await expect(view).toContainText('Weekly cap reached');
    await page.keyboard.press('Tab');
    await page.getByTestId('selection-provider').focus();
    await expect(page.getByTestId('selection-provider')).toBeFocused();
    await view.locator('button[aria-expanded]').first().focus();
    await expect(view.locator('button[aria-expanded]')).toBeFocused();
    await page.keyboard.press('Enter');
    await expect(view).toContainText('Quota snapshot exceeded the cap');
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await view.screenshot({ path: resultPath(testInfo, `model-selection-view--mocked-${theme}-1440.png`) });
    }
    await page.goto(`/?job=${encodeURIComponent(id)}&watchPath=${encodeURIComponent(watchPath)}`);
    await dismissDevErrorDialog(page);
    await expect(page.getByTestId('prompt-tab-timeline')).toBeVisible({ timeout: 45_000 });
    await page.getByTestId('prompt-tab-timeline').click({ force: true, timeout: 30_000 });
    await page.getByTestId('timeline-selection-link').click();
    await expect(view).toBeVisible();
    await expect.poll(() => new URL(page.url()).searchParams.get('msCard')).toBe(id);
    await expect.poll(() => new URL(page.url()).searchParams.get('msEvent')).toBe(receipt.id);
    await expect(view).toContainText('Weekly cap reached');
    expect(errors).toEqual([]);
  } finally {
    await api(`/api/tasks/${encodeURIComponent(id)}?watchPath=${encodeURIComponent(watchPath)}`, { method: 'DELETE' });
  }
});
