"use strict";
const { test, expect } = require("@playwright/test");
const { mkdirSync, writeFileSync } = require("node:fs");
const { join } = require("node:path");
const { setup } = require("./dispatch.fixture.cjs");
const { setTheme, sampleColours } = require("../../../../../../e2e/helpers/theme");
const { contrastRatio } = require('../../../../../../e2e/helpers/contrast');
const results = process.env.JOB_RESULTS_DIR || 'test-results/dispatch-visual';
test('review keeps server values, edits revisions, separates sources and shows attribution', async ({ page }) => {
    const fixture = await setup(page);
    const review = page.getByTestId('dispatch-review');
    await expect(review).toContainText('Gated ×0.55 · plain 81');
    await expect(review).toContainText('Not in replay');
    const conflicts = page.getByTestId('dispatch-weight-conflicts');
    await conflicts.fill('31');
    await expect(page.getByTestId('dispatch-remainder')).toHaveText('Remainder: -1 / 100');
    await expect(page.getByRole('button', { name: 'Save as new revision' })).toBeDisabled();
    await page.getByRole('button', { name: 'Reset to defaults' }).click();
    await expect(conflicts).toHaveValue('30');
    await page.getByLabel('Parallelism gate', { exact: true }).fill('0.49');
    await expect(page.getByRole('button', { name: 'Save as new revision' })).toBeDisabled();
    await page.getByLabel('Parallelism gate', { exact: true }).fill('0.8');
    fixture.failSave(true);
    await page.getByRole('button', { name: 'Save as new revision' }).click();
    await expect(page.getByRole('alert')).toContainText('Your edits are preserved');
    fixture.failSave(false);
    await page.getByRole('button', { name: 'Save as new revision' }).click();
    await expect(review).toContainText('Saved w2');
    await expect(page.getByLabel('Weights revision')).toHaveValue('w2');
    expect(fixture.writes).toHaveLength(2);
    await page.getByLabel('Weights revision').selectOption('w1');
    await expect(page.getByLabel('Weights revision')).toHaveValue('w1');
    await page.getByRole('button', { name: 'Shadow', exact: true }).click();
    await expect(page.getByTestId('dispatch-ranking')).toContainText('Surveyor');
    await expect(page.getByTestId('dispatch-ranking')).not.toContainText('Area warden');
    await page.getByRole('button', { name: 'Live', exact: true }).click();
    await page.getByText('AGT-1 and AGT-2 · merge', { exact: true }).click();
    await expect(review).toContainText('decision-1');
    await expect(review).toContainText('DispatchReviewService.cs');
    fixture.failLoad(true);
    await page.getByRole('combobox', { name: 'Window', exact: true }).selectOption({ label: '17 November 2026' });
    await expect(page.getByRole('alert')).toContainText('could not be loaded');
    fixture.failLoad(false);
    fixture.small();
    await page.getByRole('button', { name: 'Retry load' }).click();
    await expect(page.getByTestId('dispatch-ranking')).toContainText('too few cards');
});
test('viewer cannot edit weights', async ({ page }) => {
    await setup(page, false);
    await expect(page.getByTestId('dispatch-weight-conflicts')).toBeDisabled();
    await expect(page.getByRole('button', { name: 'Save as new revision' })).toBeDisabled();
});
test('review and dispatcher sheet in both themes and widths', async ({ page }) => {
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    page.on('console', message => { if (message.type() === 'error')
        errors.push(message.text() + ' ' + message.location().url); });
    page.on('response', response => { if (response.status() >= 400) errors.push(`${response.status()} ${response.url()}`); });
    await setup(page);
    mkdirSync(results, { recursive: true });
    const round = process.env.DISPATCH_VISUAL_ROUND || 'round1';
    for (const width of [1440, 390])
        for (const theme of ['light', 'dark']) {
            await page.setViewportSize({ width, height: width === 390 ? 844 : 1000 });
            await setTheme(page, theme);
            await page.getByTestId('persona-page').evaluate(el => el.scrollIntoView({ block: 'start' }));
            await expect.poll(async () => { const c = await sampleColours(page, '[data-testid="dispatch-ranking"] td', 0); return contrastRatio(c.color, c.bg); }).toBeGreaterThanOrEqual(4.5);
            await page.screenshot({ path: join(results, `${round}-review-${width}-${theme}.png`), fullPage: true, animations: 'disabled' });
            expect(await page.getByTestId('dispatch-review').evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true);
            expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
            await page.getByTestId('dispatch-weights').evaluate(el => el.scrollIntoView({ block: 'start' }));
            await page.screenshot({ path: join(results, `${round}-weights-${width}-${theme}.png`), animations: 'disabled' });
            if (width === 390) {
                await page.getByRole('button', { name: 'Save as new revision' }).evaluate(el => el.scrollIntoView({ block: 'center' }));
                await page.screenshot({ path: join(results, `${round}-save-${width}-${theme}.png`), animations: 'disabled' });
            }
            await page.getByTestId('dispatch-review').evaluate(el => el.scrollIntoView({ block: 'start' }));
        }
    await page.goto('/#/workspace/team/area-warden');
    await page.getByTestId('persona-tabs').getByRole('button', { name: 'Sheet', exact: true }).click();
    await expect(page.getByTestId('dispatch-record')).toBeVisible();
    await expect(page.getByTestId('dispatch-review')).toContainText('2026.11.1');
    for (const width of [1440, 390])
        for (const theme of ['light', 'dark']) {
            await page.setViewportSize({ width, height: width === 390 ? 844 : 1000 });
            await setTheme(page, theme);
            await page.getByTestId('persona-page').evaluate(el => el.scrollIntoView({ block: 'start' }));
            await page.screenshot({ path: join(results, `${round}-sheet-${width}-${theme}.png`), fullPage: true, animations: 'disabled' });
            expect(await page.getByTestId('dispatch-review').evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true);
            expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
            await page.getByTestId('dispatch-record').evaluate(el => el.scrollIntoView({ block: 'start' }));
            const c = await sampleColours(page, '[data-testid="dispatch-record"] strong', 0);
            expect(contrastRatio(c.color, c.bg)).toBeGreaterThanOrEqual(4.5);
            await page.screenshot({ path: join(results, `${round}-record-${width}-${theme}.png`), animations: 'disabled' });
            await page.getByTestId('dispatch-review').evaluate(el => el.scrollIntoView({ block: 'start' }));
        }
    writeFileSync(join(results, `${round}-browser-check.json`), JSON.stringify({ errors }, null, 2));
    expect(errors).toEqual([]);
});
