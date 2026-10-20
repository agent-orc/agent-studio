import { test, expect } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';

test('open orchestrator sheet fills the phone body with the sidebar present', async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 390, height: 844 });
  const errors: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('console', message => {
    if (message.type() === 'error') errors.push(message.text());
  });

  // These background probes target services outside the isolated E2E stack.
  await page.route('**/update/status', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    headers: { 'access-control-allow-origin': '*' },
    body: JSON.stringify({ phase: 'idle', isRunning: false, behindBy: 0 }),
  }));
  await page.route('**/api/v1/management/credential-inventory', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ generatedAt: '2026-10-20T00:00:00Z', items: [] }),
  }));
  await page.route('**/healthz', route => route.fulfill({ status: 200, body: 'Healthy' }));

  await page.goto('/');
  await expect(page.getByTestId('studio-sidebar')).toBeVisible();
  await page.getByTestId('orch-side-sheet-toggle').click();

  const sheetHost = page.locator('app-orchestrator-side-sheet');
  const sheet = page.getByTestId('orch-side-sheet');
  const body = page.getByTestId('studio-body');
  await expect(sheetHost).toHaveClass(/is-open/);
  await expect(sheet).toBeVisible();
  await expect(sheet.getByTestId('orch-context-badge')).toBeVisible();
  await expect(sheet.getByTestId('sidesheet-close')).toBeVisible();
  await expect(sheet.locator('main')).toBeVisible();

  await expect.poll(async () => {
    const bodyBox = await body.boundingBox();
    const host = await sheetHost.boundingBox();
    if (!bodyBox || !host) return null;
    return {
      left: Math.round(host.x - bodyBox.x),
      top: Math.round(host.y - bodyBox.y),
      width: Math.round(host.width - bodyBox.width),
      height: Math.round(host.height - bodyBox.height),
    };
  }).toEqual({ left: 0, top: 0, width: 0, height: 0 });

  const bounds = await sheet.evaluate(element => {
    const main = element.querySelector('main');
    const close = element.querySelector('[data-testid="sidesheet-close"]');
    const context = element.querySelector('[data-testid="orch-context-badge"]');
    return [main, close, context].map(node => {
      const rect = node?.getBoundingClientRect();
      return rect ? { left: rect.left, right: rect.right, top: rect.top, bottom: rect.bottom } : null;
    });
  });
  for (const rect of bounds) {
    expect(rect).not.toBeNull();
    expect(rect!.left).toBeGreaterThanOrEqual(0);
    expect(rect!.right).toBeLessThanOrEqual(390);
    expect(rect!.top).toBeGreaterThanOrEqual(0);
    expect(rect!.bottom).toBeLessThanOrEqual(844);
  }
  expect((await sheet.locator('main').innerText()).trim().length).toBeGreaterThan(0);
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(390);

  const output = process.env.JOB_RESULTS_DIR ?? testInfo.outputDir;
  mkdirSync(output, { recursive: true });
  await page.screenshot({ path: join(output, 'orchestrator-sheet-phone-390.png') });
  await sheet.getByTestId('orch-context-badge').click();
  await expect(sheet.getByTestId('orch-side-sheet-settings')).toBeVisible();
  await sheet.getByTestId('sidesheet-close').click();
  await expect(sheetHost).not.toHaveClass(/is-open/);
  expect(errors).toEqual([]);
});
