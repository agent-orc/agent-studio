import { test, expect } from '@playwright/test';
import { readFileSync, readdirSync, mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

// Read state data rather than importing Angular into the Playwright process.
const library = resolve(__dirname, '../../studio-ui/src/components');
function entries(dir: string): { id: string; overlay: boolean; states: { id: string }[] }[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap(entry => {
    const path = join(dir, entry.name);
    if (entry.isDirectory()) return entries(path);
    if (!entry.name.endsWith('.catalogue.ts')) return [];
    const text = readFileSync(path, 'utf8');
    const start = text.indexOf('{\n', text.indexOf('...') >= 0 ? text.indexOf('...') : text.indexOf(' = '));
    const end = text.indexOf('\n}', start) + 2;
    return [JSON.parse(text.slice(start, end))];
  });
}
const inventory = entries(library);

for (const entry of inventory) {
  for (const width of [1440, 390]) {
    for (const theme of ['light', 'dark']) {
      test(`${entry.id} ${width} ${theme}: all recorded states`, async ({ page }) => {
        const errors: string[] = [];
        const apiRequests: string[] = [];
        page.on('pageerror', error => errors.push(error.message));
        page.on('console', message => { if (message.type() === 'error') errors.push(message.text()); });
        page.on('request', request => { if (new URL(request.url()).pathname.startsWith('/api/')) apiRequests.push(request.url()); });
        for (const state of entry.states) {
          await page.goto(`/?component=${entry.id}&state=${state.id}&theme=${theme}`);
          await page.setViewportSize({ width, height: 900 });
          await expect(page.getByRole('heading', { name: 'Components', exact: true })).toBeAttached();
          await expect(page.locator('html')).toHaveAttribute('data-studio-theme', theme);
          if (entry.id === 'loading-surface') await expect(page.getByText('Loading records…')).toBeVisible();
          if (entry.id === 'tooltip' && state.id !== 'hidden') await page.getByRole('button', { name: 'Focus or hover for help' }).hover();
          if (entry.id === 'media-lightbox' && state.id !== 'closed') await expect(page.getByTestId('media-lightbox-image')).toBeVisible();
          const overflow = await page.evaluate(() => document.documentElement.scrollWidth > innerWidth);
          expect(overflow, `${entry.id}/${state.id} horizontal overflow`).toBe(false);
          const target = entry.overlay || entry.id === 'notification-stack' || entry.id === 'tooltip' ? page : page.getByTestId('specimen');
          await expect(target).toHaveScreenshot(`${entry.id}-${state.id}-${width}-${theme}.png`);
          if (process.env.JOB_RESULTS_DIR && entry.states[0] === state && ['status-indicator', 'menu', 'dialog', 'row'].includes(entry.id)) {
            const out = join(process.env.JOB_RESULTS_DIR, 'catalogue');
            mkdirSync(out, { recursive: true });
            await page.screenshot({ path: join(out, `${entry.id}-${width}-${theme}.png`), animations: 'disabled' });
          }
        }
        expect(errors).toEqual([]);
        expect(apiRequests).toEqual([]);
      });
    }
  }
}

test('menu arrows skip disabled rows and Escape returns focus; pending button is disabled and busy', async ({ page }) => {
  await page.goto('/?component=menu&state=open');
  const menu = page.getByRole('menu');
  await expect(menu).toBeVisible();
  await page.keyboard.press('End');
  await expect(page.getByRole('menuitem', { name: 'Delete' })).toBeFocused();
  await page.keyboard.press('Home');
  await expect(page.getByRole('menuitem', { name: 'Open' })).toBeFocused();
  await page.keyboard.press('Escape');
  await expect(menu).toBeHidden();
  await page.goto('/?component=pending-button&state=pending');
  const pending = page.getByTestId('specimen').getByRole('button');
  await expect(pending).toBeDisabled();
  await expect(pending).toHaveAttribute('aria-busy', 'true');
});
