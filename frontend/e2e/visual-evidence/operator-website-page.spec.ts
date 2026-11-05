import { test, expect } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';

const root = path.resolve(__dirname, '..', '..', '..');
const pageFile = path.join(root, 'docs/website/operator-agent/index.html');

test('operator website page presents every pinned scene in both themes', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto(`file://${pageFile}`);
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Operate Studio from your coding agent');
  await expect(page.locator('main section')).toHaveCount(6);
  const images = page.locator('main img');
  await expect(images).toHaveCount(14);
  await images.evaluateAll(items => items.forEach(item => (item as HTMLImageElement).loading = 'eager'));
  await expect.poll(async () => images.evaluateAll(items => items.every(item => (item as HTMLImageElement).naturalWidth === 3840))).toBe(true);

  for (const theme of ['light', 'dark'] as const) {
    await page.emulateMedia({ colorScheme: theme });
    const paper = await page.locator('body').evaluate(element => getComputedStyle(element).backgroundColor);
    expect(paper).toBe(theme === 'light' ? 'rgb(248, 249, 251)' : 'rgb(21, 26, 32)');
    const name = `operator-website-page-${theme}.png`;
    const output = process.env.JOB_RESULTS_DIR;
    if (output) fs.mkdirSync(output, { recursive: true });
    await page.screenshot({ path: output ? path.join(output, name) : undefined, fullPage: true, animations: 'disabled', scale: 'css' });
  }
});
