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

// AGT-4174: absence must remove the flex item, while populated states keep
// their existing accessible labels. Geometry is relative to the preceding
// integration label so unrelated shell offsets do not weaken the regression.
for (const state of ['omitted', 'null', 'live', 'integrated-not-live', 'unknown'] as const) {
  test(`Delivery live-state layout: ${state}`, async ({ page }) => {
    const errors: string[] = [];
    page.on('pageerror', error => errors.push(error.message));
    await openBoard(page);
    await page.route('**/api/tasks/*/delivery-claim**', route => route.fulfill({
      json: {
        taskKey: MERGED.taskKey, jobId: MERGED.id, lane: MERGED.state, deliveryRef: 'task/WEB-47',
        integrationBranch: 'develop', releaseBranch: 'main', containmentStatus: 'contained', integrated: true,
        released: false, integratedSha: 'bbbbbbb', mergeCommit: 'eeeeeee',
        hasIntegrationRecord: true, class: 'integrated-delivery', findings: [], completionClaim: null,
        ...(state === 'omitted' ? {} : { studioLive: state === 'null' ? null : {
          state, revision: 'abcdef0123456789', sinceUtc: '2026-11-26T12:00:00Z',
        } }),
      },
    }));
    await card(page, MERGED.title).first().click();
    const panel = page.getByTestId('delivery-claim-panel');
    await expect(page.getByTestId('delivery-claim-merge')).toHaveText('via eeeeeee');
    await page.evaluate(() => document.fonts.ready);
    const out = process.env.JOB_RESULTS_DIR ?? 'test-results';
    const phase = process.env.DELIVERY_LAYOUT_CAPTURE_PHASE ?? 'after';
    mkdirSync(join(out, phase), { recursive: true });
    for (const width of [1440, 390]) {
      await page.setViewportSize({ width, height: 900 });
      for (const theme of ['light', 'dark'] as const) {
        await setTheme(page, theme);
        await page.mouse.move(0, 0);
        await panel.scrollIntoViewIfNeeded();
        await page.screenshot({
          path: join(out, phase, `delivery-${state}-${width}-${theme}--mocked.png`),
          animations: 'disabled', caret: 'hide',
        });
        const geometry = await panel.evaluate(element => {
          const integration = element.querySelector('[data-testid="delivery-claim-integration"]')!;
          const merge = element.querySelector('[data-testid="delivery-claim-merge"]')!;
          const a = integration.getBoundingClientRect();
          const b = merge.getBoundingClientRect();
          return { gap: getComputedStyle(element).columnGap, integrationRight: a.right,
            integrationY: a.y, mergeX: b.x, mergeY: b.y,
            adjacent: integration.nextElementSibling === merge };
        });
        writeFileSync(join(out, phase, `delivery-${state}-${width}-${theme}.json`), JSON.stringify(geometry));
        expect.soft(geometry.gap).toBe('8px');
        const indicator = panel.getByTestId('studio-card-live-state');
        if (state === 'omitted' || state === 'null') {
          expect.soft(geometry.adjacent).toBe(true);
          expect.soft(Math.abs(geometry.mergeY - geometry.integrationY)).toBeLessThan(2);
          expect.soft(geometry.mergeX - geometry.integrationRight).toBeCloseTo(8, 1);
          await expect.soft(indicator).toHaveCount(0);
        } else {
          const label = state === 'live' ? 'Live since abcdef0123'
            : state === 'integrated-not-live' ? 'Integrated, not live' : 'Live state not checked yet';
          await expect(indicator).toHaveText(label);
          await expect(indicator).toMatchAriaSnapshot(`- text: ${label}`);
        }
      }
    }
    expect(errors).toEqual([]);
  });
}
