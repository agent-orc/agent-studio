import { expect, test } from '@playwright/test';
import { join } from 'node:path';
import { mkdirSync } from 'node:fs';
import { setTheme } from '../helpers/theme';

test('owner-day batch is inspectable through the real panel in light, phone and dark', async ({ page }) => {
  test.skip(process.env.PW_E2E_PROFILE !== 'owner-day', 'Requires the isolated owner-day backend.');
  test.setTimeout(180_000);
  const shots = process.env.JOB_RESULTS_DIR;
  await page.goto('/');
  await page.getByRole('tab', { name: 'All projects · Board' }).click();
  for (const [width, theme] of [[1280, 'light'], [390, 'light'], [1280, 'dark']] as const) {
    await page.setViewportSize({ width, height: 850 });
    await setTheme(page, theme);
    if (width === 390)
      await page.getByTestId('mobile-lane-picker').getByRole('combobox', { name: 'Board lane' })
        .selectOption('4-auto-review');
    const indicator = page.getByTestId('batch-lane-indicator').filter({ hasText: 'Demo App' }).first();
    await expect(indicator).toBeVisible();
    await expect(indicator).toContainText('red');
    await indicator.click();
    const panel = page.getByTestId('batch-panel');
    await expect(panel).toBeVisible();
    await expect(panel).toContainText('demo-report-batch-1');
    await expect(panel).toContainText('DEMO-20');
    await expect(panel).toContainText('DEMO-24');
    await expect(panel).toContainText('Phase: red');
    await expect(panel).toContainText('ReportExportTests.EmptyReportKeepsExportDisabled');
    await expect(panel).toContainText('owner triage and isolation are waiting');
    if (shots) {
      mkdirSync(shots, { recursive: true });
      await panel.screenshot({ path: join(shots, `owner-day-batch-${width}-${theme}.png`), animations: 'disabled' });
    }
    await panel.getByTestId('batch-panel-body').evaluate(element => { element.scrollTop = element.scrollHeight; });
    await expect(panel.getByTestId('batch-phase')).toBeVisible();
    await expect(panel.getByTestId('batch-phase')).toContainText('owner triage and isolation are waiting');
    if (shots)
      await panel.screenshot({ path: join(shots, `owner-day-batch-phase-${width}-${theme}.png`), animations: 'disabled' });
    await panel.getByRole('button', { name: 'Close batch panel' }).click();
  }
});

// F11: with the production service worker in control, a backend document
// opened in a new tab must show the backend text, not the Studio shell.
test.describe('batch evidence links with the service worker enabled', () => {
  test.use({ serviceWorkers: 'allow' });

  test('run evidence, run log and isolation evidence open as text in a new tab', async ({ page }) => {
    test.skip(process.env.PW_E2E_PROFILE !== 'owner-day', 'Requires the isolated owner-day backend.');
    test.setTimeout(120_000);
    await page.goto('/');
    await page.evaluate(() => navigator.serviceWorker.ready);
    await page.reload();
    await expect.poll(() => page.evaluate(() => navigator.serviceWorker.controller !== null), { timeout: 30_000 }).toBe(true);
    await page.getByRole('tab', { name: 'All projects · Board' }).click();
    await page.getByTestId('batch-lane-indicator').filter({ hasText: 'Demo App' }).first().click();
    const panel = page.getByTestId('batch-panel');
    await expect(panel).toBeVisible();
    const provenRed = panel.getByTestId('batch-isolation-member').filter({ has: page.getByTestId('batch-proven-red') }).first();
    const links = [
      { link: panel.getByRole('link', { name: 'Evidence', exact: true }).first(), text: 'ReportExportTests.EmptyReportKeepsExportDisabled failed' },
      { link: panel.getByRole('link', { name: 'Run log', exact: true }).first(), text: 'ReportExportTests.EmptyReportKeepsExportDisabled failed' },
      { link: provenRed.getByTestId('batch-proven-red-evidence'), text: 'failed in the simulated report suite' },
    ];
    for (const { link, text } of links) {
      await link.scrollIntoViewIfNeeded();
      const [tab] = await Promise.all([page.waitForEvent('popup'), link.click()]);
      await tab.waitForLoadState();
      expect(new URL(tab.url()).pathname).toMatch(/^\/api\/projects\/Demo%20App\/batch-gate\//);
      expect(tab.url()).not.toContain('#/');
      await expect(tab.locator('body')).toContainText(text);
      await expect(tab.locator('app-root')).toHaveCount(0);
      await tab.close();
    }
  });
});
