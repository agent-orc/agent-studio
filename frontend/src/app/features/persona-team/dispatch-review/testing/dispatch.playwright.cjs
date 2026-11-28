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
test('keyboard traverses the ranking and edits and saves a weight revision', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 1000 });
    const fixture = await setup(page);
    const review = page.getByTestId('dispatch-review');
    const ranking = page.getByTestId('dispatch-ranking');
    const links = ranking.getByRole('link');
    await expect(links).toHaveCount(5);
    await review.getByRole('button', { name: 'Live', exact: true }).focus();
    async function tabTo(control) {
        await page.keyboard.press('Tab');
        await expect(control).toBeFocused();
    }
    for (const link of await links.all()) await tabTo(link);
    mkdirSync(results, { recursive: true });
    await ranking.screenshot({ path: join(results, 'keyboard-ranking--mocked.png') });
    for (const summary of await review.locator('summary').filter({ hasText: 'raw evidence and limits' }).all()) await tabTo(summary);
    const weights = page.getByTestId('dispatch-weights');
    const fields = weights.getByRole('spinbutton');
    await expect(fields).toHaveCount(9);
    await tabTo(fields.nth(0));
    await page.keyboard.press('ArrowUp');
    await expect(page.getByTestId('dispatch-remainder')).toHaveText('Remainder: -1 / 100');
    await tabTo(fields.nth(1));
    await page.keyboard.press('ArrowDown');
    for (const field of (await fields.all()).slice(2)) await tabTo(field);
    await tabTo(weights.getByRole('textbox'));
    await page.keyboard.type('Keyboard revision');
    await tabTo(weights.getByRole('button', { name: 'Save as new revision' }));
    await weights.screenshot({ path: join(results, 'keyboard-weights--mocked.png') });
    await page.keyboard.press('Enter');
    await expect(review).toContainText('Saved w2');
    expect(fixture.writes).toHaveLength(1);
    expect(fixture.writes[0]).toMatchObject({ weights: { conflicts: 31, tokens: 24 }, reason: 'Keyboard revision' });
});
test('viewer cannot edit weights', async ({ page }) => {
    await setup(page, false);
    await expect(page.getByTestId('dispatch-weight-conflicts')).toBeDisabled();
    await expect(page.getByRole('button', { name: 'Save as new revision' })).toBeDisabled();
    await expect(page.getByTestId('dispatch-pin')).toBeDisabled();
    await expect(page.getByTestId('dispatch-stop')).toBeDisabled();
});
test('owner controls carry the fence, retain failures and undo the named guard', async ({ page }) => {
    const fixture = await setup(page);
    const controls = page.getByTestId('dispatch-lease-controls');
    await expect(controls).toContainText('wave:playthrough-17');
    fixture.failControl(true);
    await page.getByTestId('dispatch-pin').click();
    await expect(page.getByRole('alert')).toContainText('Reload the review');
    await expect(controls).toContainText('fence 1');
    fixture.failControl(false);
    await page.getByTestId('dispatch-stop').click();
    await expect(controls).toContainText('Alternation stopped');
    await expect(page.getByTestId('dispatch-stop')).toBeDisabled();
    expect(fixture.controls.at(-1)).toMatchObject({ action: 'stop', fence: 1 });
    await page.getByTestId('dispatch-pin').click();
    await expect(controls).toContainText('fence 3');
    expect(fixture.controls.at(-1)).toMatchObject({ action: 'pin', persona: 'queue-keeper', fence: 2 });
    fixture.guard();
    await page.getByRole('combobox', { name: 'Window', exact: true }).selectOption({ label: '17 November 2026' });
    await expect(controls).toContainText('Guard fallback active');
    mkdirSync(results, { recursive: true });
    for (const width of [1440, 390]) for (const theme of ['light', 'dark']) {
        await page.setViewportSize({ width, height: width === 390 ? 844 : 1000 });
        await setTheme(page, theme);
        await controls.evaluate(el => el.scrollIntoView({ block: 'center' }));
        await page.screenshot({ path: join(results, `lease-controls-${width}-${theme}.png`), animations: 'disabled' });
        expect(await controls.evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true);
    }
    await page.getByTestId('dispatch-undo-guard').click();
    await expect(controls).not.toContainText('Guard fallback active');
    await expect(controls).toContainText('pinned to Area warden');
    expect(fixture.controls.at(-1)).toMatchObject({ action: 'undo-guard', guardId: 'guard-1', fence: 3 });
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
