import { test, expect } from '@playwright/test';
import { api } from './helpers/api';
import { createJob } from './helpers/jobs';
import { dismissDevErrorDialog } from './helpers/theme';

test('debug output storm', async ({ page }) => {
  test.setTimeout(120_000);
  const watchPath = (await api<{ path: string }[]>('/api/watch-paths'))[0].path;
  const ids = [1, 2].map(i => `e2e-dbg-${Date.now()}-${i}`);
  for (const id of ids) await createJob({ id, title: id, watchPath, targetState: '5-human-review', promptMarkdown: '# hi', requiresIntegration: false, fixture: false });
  const counts = new Map<string, number>();
  let outputs = 0;
  page.on('request', r => { if (r.url().includes('/output')) outputs++; });
  page.on('console', m => { if (m.text().startsWith('DBGFX')) counts.set(m.text(), (counts.get(m.text()) ?? 0) + 1); });
  try {
    await page.goto('/');
    await dismissDevErrorDialog(page);
    const card = page.getByTestId('task-card').filter({ hasText: ids[0] }).first();
    await expect(card).toBeVisible({ timeout: 30000 });
    const box = (await card.boundingBox())!;
    await card.click({ position: { x: box.width / 2, y: box.height - 4 }, force: true });
    await page.waitForTimeout(4000);
    console.log('AFTER OPEN outputs', outputs, JSON.stringify([...counts]));
    for (let i = 0; i < 4; i++) { await page.keyboard.press(i % 2 ? 'k' : 'j'); await page.waitForTimeout(1500); }
    console.log('AFTER SWITCH outputs', outputs, JSON.stringify([...counts]));
    await page.waitForTimeout(4000);
    console.log('IDLE outputs', outputs, JSON.stringify([...counts]));
  } finally {
    for (const id of ids) await api(`/api/tasks/${id}?watchPath=${encodeURIComponent(watchPath)}`, { method: 'DELETE' }).catch(() => undefined);
  }
});
