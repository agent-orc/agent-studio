import { test, expect } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { openBoard, openDeploy, card, MERGED, installRoutes } from '../helpers/ui-foundation-surfaces';
import { stubNetworkedOwner, openAccess } from '../helpers/ui-foundation-access';
import { setTheme } from '../helpers/theme';

// Identical fixtures, time, viewports and capture points before and after a
// mechanical migration. The image comparison report is written separately.
test.use({ locale: 'en-US', timezoneId: 'UTC' });

for (const surface of ['board', 'card-detail', 'operations', 'access']) {
  test(`UI foundation preserves ${surface}`, async ({ page }) => {
    test.setTimeout(120_000);
    const errors: string[] = [];
    page.on('pageerror', error => errors.push(error.message));
    page.on('console', message => { if (message.type() === 'error' && /(?:ERROR|TypeError|NG0)/.test(message.text())) errors.push(message.text()); });
    if (surface === 'access') {
      await installRoutes(page);
      await page.clock.setFixedTime(new Date('2026-10-20T12:00:00Z'));
      await page.emulateMedia({ reducedMotion: 'reduce' });
      await stubNetworkedOwner(page);
      await openAccess(page);
    } else if (surface === 'operations') {
      await openDeploy(page);
      await page.getByTestId('deploy-tab-operations').click();
      await expect(page.getByTestId('project-operations')).toBeVisible();
    } else {
      await openBoard(page);
      if (surface === 'card-detail') {
        await card(page, MERGED.title).first().click();
        await expect(page.getByTestId('prompt-tab-overview')).toBeVisible();
      }
    }
    await page.waitForLoadState('networkidle');
    await page.evaluate(() => document.fonts.ready);
    await expect(page.getByTestId('offline-banner-state')).toHaveCount(0);
    await expect(page.getByTestId('error-dialog-overlay')).toHaveCount(0);
    const out = process.env.JOB_RESULTS_DIR ?? 'test-results';
    mkdirSync(out, { recursive: true });
    for (const width of [1440, 390]) {
      await page.setViewportSize({ width, height: 900 });
      for (const theme of ['light', 'dark'] as const) {
        await setTheme(page, theme);
        await page.mouse.move(0, 0);
        // Match the strip's scroll position after responsive viewport changes.
        await page.locator('.studio-tabbar__list').evaluateAll(nodes => nodes.forEach(node => { node.scrollLeft = 0; }));
        // Let theme styles and responsive Angular views finish their paint.
        await page.evaluate(() => new Promise<void>(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve()))));
        await expect(page.getByTestId('error-dialog-overlay')).toHaveCount(0);
        await expect(page.getByTestId('offline-banner-state')).toHaveCount(0);
        await page.screenshot({ path: join(out, `${surface}-${width}-${theme}.png`), animations: 'disabled', caret: 'hide' });
        const facts = await page.evaluate(() => [...document.body.querySelectorAll('*')].flatMap(element => {
          const rect = element.getBoundingClientRect();
          const style = getComputedStyle(element);
          if (!rect.width || !rect.height || rect.bottom <= 0 || rect.top >= innerHeight || rect.right <= 0 || rect.left >= innerWidth || style.visibility === 'hidden') return [];
          return [{ tag: element.tagName, testId: element.getAttribute('data-testid'),
            box: [rect.x, rect.y, rect.width, rect.height].map(value => Math.round(value * 1000) / 1000),
            styles: Object.fromEntries(['fontSize', 'fontFamily', 'fontWeight', 'lineHeight', 'paddingTop', 'paddingRight', 'paddingBottom', 'paddingLeft', 'marginTop', 'marginRight', 'marginBottom', 'marginLeft', 'columnGap', 'rowGap', 'borderRadius', 'boxShadow', 'color', 'backgroundColor'].map(property => [property, style[property as keyof CSSStyleDeclaration]])),
          }];
        }));
        writeFileSync(join(out, `${surface}-${width}-${theme}-styles.json`), JSON.stringify(facts));
      }
    }
    expect(errors).toEqual([]);
  });
}
