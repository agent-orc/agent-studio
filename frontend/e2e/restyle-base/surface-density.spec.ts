import { expect, test, type Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';

const densityKey = 'atp.studio.surfaceDensity.v1';
const aliases = ['row-pad-block', 'row-pad-inline', 'row-gap', 'section-gap', 'row-min-h', 'row-min-h-interactive'];
const values = {
  compact: ['3px', '6px', '3px', '10px', '20px', '32px'],
  default: ['6px', '10px', '6px', '16px', '24px', '36px'],
  cozy: ['10px', '14px', '10px', '24px', '32px', '40px'],
};
type Density = keyof typeof values;

async function expectDensity(page: Page, density: Density) {
  await expect(page.locator('html')).toHaveAttribute('data-studio-density', density);
  await expect.poll(() => page.evaluate(names => {
    const style = getComputedStyle(document.documentElement);
    return names.map(name => style.getPropertyValue(`--studio-${name}`).trim());
  }, aliases)).toEqual(values[density]);
  await expect(page.getByTestId(`settings-density-${density}`)).toHaveAttribute('aria-pressed', 'true');
}

for (const width of [1280, 390]) {
  for (const theme of ['light', 'dark'] as const) {
    test(`surface density: ${theme} at ${width}px, compact baseline, keyboard, reload and sibling tabs`, async ({ page, context }, testInfo) => {
      await page.setViewportSize({ width, height: 844 });
      await page.goto('/#/workspace/settings/appearance');
      await expect(page.getByTestId('appearance-settings')).toBeVisible();
      await page.getByTestId(`settings-theme-${theme}`).click();
      await expect(page.locator('html')).toHaveAttribute('data-studio-theme', theme);
      // Exact pre-change root geometry, independently of the selected theme.
      await expectDensity(page, 'compact');
      const sibling = await context.newPage();
      await sibling.goto('/#/workspace/settings/appearance');
      await expectDensity(sibling, 'compact');

      for (const density of ['default', 'cozy', 'compact'] as const) {
        const button = page.getByTestId(`settings-density-${density}`);
        await button.focus();
        await expect(button).toBeFocused();
        const box = await button.boundingBox();
        expect(box!.height).toBeGreaterThanOrEqual(24);
        await page.keyboard.press('Enter');
        await expectDensity(page, density);
        await expect(button).toBeFocused();
        await expectDensity(sibling, density);
        expect(await page.evaluate(key => localStorage.getItem(key), densityKey)).toBe(density);
        await page.reload();
        await expectDensity(page, density);
        await expect(page.locator('html')).toHaveAttribute('data-studio-theme', theme);
        const group = page.getByRole('group', { name: 'Surface density', exact: true });
        await group.scrollIntoViewIfNeeded();
        const bounds = await group.boundingBox();
        expect(bounds!.x).toBeGreaterThanOrEqual(0);
        expect(bounds!.x + bounds!.width).toBeLessThanOrEqual(width + 1);
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
        const output = process.env.JOB_RESULTS_DIR ?? testInfo.outputDir;
        mkdirSync(output, { recursive: true });
        await page.getByTestId('appearance-settings').screenshot({
          path: join(output, `surface-density-${density}-${theme}-${width}--real.png`), animations: 'disabled',
        });
      }
      await page.evaluate(key => localStorage.removeItem(key), densityKey);
      await expectDensity(sibling, 'compact');
      await sibling.close();
    });
  }
}

// All current app rows explicitly select a local variant. A real Access row
// loads the shared stylesheet; changing only its host attributes probes the
// inherited/local CSS contracts. Unit tests cover Angular's input reflection.
for (const theme of ['light', 'dark'] as const) {
  for (const width of [1280, 390]) {
    test(`shared row CSS: inherited density, explicit local variants, unchanged data in ${theme} at ${width}px`, async ({ page, context }, testInfo) => {
      await page.setViewportSize({ width, height: 844 });
      const person = { id: 'density-person', username: 'density.reader', displayName: 'Density Reader', role: 'owner', projects: [], disabled: false, hasFactor: true };
      const iam = { roles: [], permissions: [], iamVersion: 1, users: [person], groups: [], serviceAccounts: [], bindings: [], invitations: [] };
      const status = { profile: 'networked', bootstrapRequired: false, authenticated: true, user: person };
      // The Access fixture must own every auth read. An unmocked protected
      // endpoint returns 401 from the local backend and expires the mock session.
      await page.route('**/api/auth/**', route => route.fulfill({ status: 404, json: { message: 'Not found' } }));
      await page.route('**/hubs/v1/studio/negotiate?*', route => route.fulfill({ json: {
        negotiateVersion: 1, connectionId: 'density-e2e', connectionToken: 'density-e2e',
        availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text', 'Binary'] }],
      } }));
      await page.routeWebSocket('**/hubs/v1/studio*', socket => {
        socket.onMessage(message => {
          if (typeof message === 'string' && message.includes('"protocol":"json"')) socket.send('{}\u001e');
        });
      });
      await page.route('**/api/auth/sessions**', route => route.fulfill({ json: [] }));
      await page.route('**/api/auth/runners', route => route.fulfill({ json: [] }));
      await page.route('**/api/auth/runner-enrollments', route => route.fulfill({ json: [] }));
      await page.route('**/api/auth/status', route => route.fulfill({ json: status }));
      await page.route('**/api/v1/studio/auth/status', route => route.fulfill({ json: status }));
      await page.route('**/api/auth/iam', route => route.fulfill({ json: iam }));
      await page.route('**/api/auth/users', route => route.fulfill({ json: [person] }));
      await page.goto('/#/workspace/settings/access');
      await page.getByRole('button', { name: 'Open details of Density Reader', exact: true }).click();
      const row = page.getByTestId('access-person-detail').getByTestId('app-row').nth(1);
      await expect(row).toBeVisible();
      const content = await row.innerText();
      const preference = await context.newPage();
      await preference.goto('/#/workspace/settings/appearance');
      await preference.getByTestId(`settings-theme-${theme}`).click();
      // Apply the same theme in this tab without claiming cross-tab theme sync.
      await page.evaluate(theme => document.documentElement.dataset['studioTheme'] = theme, theme);

      for (const density of ['compact', 'default', 'cozy'] as const) {
        await preference.getByTestId(`settings-density-${density}`).click();
        await expect(page.locator('html')).toHaveAttribute('data-studio-density', density);
        for (const variant of [null, 'compact', 'default', 'cozy'] as const) {
          await row.evaluate((inner, variant) => {
            const host = inner.parentElement!;
            if (variant) host.setAttribute('data-variant', variant);
            else host.removeAttribute('data-variant');
          }, variant);
          const expected = variant ?? density;
          await expect(row).toHaveCSS('padding-top', values[expected][0]);
          await expect(row).toHaveCSS('min-height', values[expected][4]);
          // Preserve the primitive's 8 px compact gap and flush inline padding.
          await expect(row).toHaveCSS('column-gap', expected === 'cozy' ? '10px' : '8px');
          await expect(row).toHaveCSS('padding-left', '0px');
          await expect(row.getByTestId('app-row-value')).toHaveCSS('column-gap', expected === 'cozy' ? '10px' : '6px');
          await expect(row.getByTestId('app-row-trailing')).toHaveCSS('column-gap', expected === 'compact' ? '4px' : expected === 'default' ? '6px' : '10px');
          expect(await row.innerText()).toBe(content);
          await row.evaluate(inner => inner.parentElement!.setAttribute('data-interactive', ''));
          await expect(row).toHaveCSS('min-height', values[expected][5]);
          await row.evaluate(inner => inner.parentElement!.removeAttribute('data-interactive'));
        }
      }
      // A containing surface can override inherited aliases; an explicit
      // row variant still wins. This remains a CSS contract probe.
      await row.evaluate(inner => {
        const host = inner.parentElement!;
        host.removeAttribute('data-variant');
        host.parentElement!.style.setProperty('--studio-row-pad-block', 'var(--studio-row-pad-block-default)');
      });
      await expect(row).toHaveCSS('padding-top', '6px');
      await row.evaluate(inner => inner.parentElement!.setAttribute('data-variant', 'compact'));
      await expect(row).toHaveCSS('padding-top', '3px');
      await row.evaluate(inner => inner.parentElement!.parentElement!.style.removeProperty('--studio-row-pad-block'));
      const bounds = await row.boundingBox();
      expect(bounds!.x).toBeGreaterThanOrEqual(0);
      expect(bounds!.x + bounds!.width).toBeLessThanOrEqual(width + 1);
      const output = process.env.JOB_RESULTS_DIR ?? testInfo.outputDir;
      mkdirSync(output, { recursive: true });
      await page.getByTestId('access-detail-panel').screenshot({
        path: join(output, `surface-density-local-row-${theme}-${width}--mocked.png`), animations: 'disabled',
      });
      await preference.close();
    });
  }
}
