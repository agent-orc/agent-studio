import { test, expect } from '@playwright/test';
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { setTheme } from '../helpers/theme';

// Run with PW_E2E_PROFILE=owner-day. The backend, quota snapshots, and receipt
// projection are real; only the disposable workspace's card receipts are seeded.
test('footer provider weeks show receipts from each real reset window', async ({ page, request }) => {
  test.setTimeout(180_000);
  const root = process.env.PW_E2E_WORKSPACE;
  expect(root).toBeTruthy();
  const quotaResponse = await request.get('/api/cli/quota');
  expect(quotaResponse.ok()).toBe(true);
  const report = await quotaResponse.json() as { snapshots: Array<{
    cliType: string; windows: Array<{ label: string; usedPct: number; resetAt: string; observedStartAt: string }>;
  }> };
  const weeks = (['claude', 'codex'] as const).map(cli => {
    const snapshot = report.snapshots.find(item => item.cliType === cli);
    const window = snapshot?.windows.find(item => /week/i.test(item.label));
    expect(window, `${cli} weekly quota`).toBeTruthy();
    return { cli, window: window! };
  });
  expect(weeks[0].window.resetAt).not.toBe(weeks[1].window.resetAt);
  expect(weeks.every(({ window }) => new Date(window.resetAt).getUTCDay() !== 1)).toBe(true);

  const inside = new Date(Date.now() - 60_000).toISOString();
  const seeded = [
    { ...weeks[0], card: 'DEMO-24', model: 'claude-provider-week-proof', tokens: 137 },
    { ...weeks[1], card: 'DEMO-25', model: 'codex-provider-week-proof', tokens: 283 },
  ];
  for (const row of seeded) {
    const file = join(root!, 'projects', 'demo-app', 'tasks', '000', row.card, 'task.json');
    const card = JSON.parse(readFileSync(file, 'utf8'));
    const entry = (at: string, model: string) => ({
      ts: at, model, participantId: `agent:${row.cli}`, cliType: row.cli,
      inputTokens: row.tokens - 7, outputTokens: 7, cacheReadTokens: 0, cacheCreationTokens: 0,
    });
    card.tokenSummary.entries = [
      entry(new Date(Date.parse(row.window.observedStartAt) - 60_000).toISOString(), `${row.model}-outside`),
      entry(inside, row.model),
    ];
    writeFileSync(file, JSON.stringify(card, null, 2) + '\n');
  }

  for (const row of seeded) {
    const response = await request.get(`/api/workspace/tokens/subscription-week?cli=${row.cli}`);
    expect(response.ok(), await response.text()).toBe(true);
    const week = await response.json() as { startUtc: string; resetAtUtc: string; usedPct: number; receipts: Array<{ model: string; card: string }> };
    expect(Date.parse(week.startUtc)).toBe(Date.parse(row.window.observedStartAt));
    expect(Date.parse(week.resetAtUtc)).toBe(Date.parse(row.window.resetAt));
    expect(week.usedPct).toBe(row.window.usedPct);
    expect(week.receipts).toEqual(expect.arrayContaining([expect.objectContaining({ model: row.model, card: row.card })]));
    expect(week.receipts.some(receipt => receipt.model === `${row.model}-outside`)).toBe(false);
  }

  await page.goto('/');
  const shots = join(process.env.JOB_RESULTS_DIR ?? 'test-results', 'provider-week-real');
  mkdirSync(shots, { recursive: true });
  for (const row of seeded) {
    await expect(page.getByTestId(`footer-usage-${row.cli}`)).toContainText(`WK ${row.window.usedPct}%`);
    await page.getByTestId(`footer-usage-${row.cli}`).click();
    await expect(page.getByTestId('subscription-week-pct')).toHaveText(`${row.window.usedPct}%`);
    await expect(page.getByTestId('subscription-week-period')).toContainText('Provider week: start');
    await expect(page.getByTestId('subscription-week-receipts')).toContainText(row.model);
    await expect(page.getByTestId('subscription-week-receipts')).not.toContainText(`${row.model}-outside`);
    await expect(page.getByTestId('subscription-week-receipts')).not.toContainText(seeded.find(other => other.cli !== row.cli)!.model);
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.getByTestId('subscription-week').screenshot({ path: join(shots, `${row.cli}-${theme}.png`), animations: 'disabled' });
    }
    await page.goto('/');
  }
});
