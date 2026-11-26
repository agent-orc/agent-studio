import { setThemeThroughControl } from '../helpers/theme';
import { test, expect } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';

const states = [
  { name: 'green', developState: 'green', count: 0, lineStop: null, tone: 'success', label: 'Develop green' },
  { name: 'quarantined', developState: 'green', count: 2, lineStop: null, tone: 'warning', label: 'Develop green · 2 quarantined' },
  { name: 'running', developState: 'running', count: 0, lineStop: null, tone: 'running', label: 'Develop running' },
  { name: 'stopped', developState: 'red', count: 0, lineStop: 'Base suite failed twice', tone: 'danger', label: 'Develop stopped · Base suite failed twice' },
];
for (const state of states) {
  test(`develop ${state.name} uses shared theme tokens at desktop and phone widths`, async ({ page }) => {
    await page.route('**/api/develop-states', route => route.fulfill({ json: [{
      project: 'Agent Studio Worktree', developState: state.developState, lastGreenSha: 'a'.repeat(40),
      quarantinedCount: state.count, lineStop: state.lineStop,
      quarantines: state.count ? [{ id: 'held', test: 'FixtureTests.Concurrent', kind: 'held', expiresAtUtc: '2026-11-27T12:00:00Z' },
        { id: 'report', test: 'ReviewTests.Mixed', kind: 'report-only', expiresAtUtc: '2026-12-02T12:00:00Z' }] : [],
    }] }));
    await page.goto('/');
    const indicator = page.getByTestId('board-develop-state');
    await expect(indicator).toHaveText(state.label);
    await expect(indicator).toHaveAttribute('data-tone', state.tone);
    for (const width of [1280, 1024, 390]) {
      await page.setViewportSize({ width, height: 844 });
      for (const theme of ['light', 'dark'] as const) {
        await setThemeThroughControl(page, theme);
        const colors = await indicator.evaluate(element => {
          const css = getComputedStyle(element);
          const probe = document.createElement('span');
          probe.style.color = css.getPropertyValue('--status-tone');
          element.append(probe);
          const expected = getComputedStyle(probe).color;
          probe.remove();
          return { actual: css.color, expected, token: css.getPropertyValue('--status-tone').trim(), background: css.backgroundColor };
        });
        expect(colors.token).not.toBe('');
        expect(colors.actual).toBe(colors.expected);
        expect(colors.background).not.toBe('rgba(0, 0, 0, 0)');
        if (state.count) {
          await indicator.focus();
          await expect(page.getByRole('tooltip')).toContainText('FixtureTests.Concurrent · held · ends 2026-11-27');
          await expect(page.getByRole('tooltip')).toContainText('ReviewTests.Mixed · report-only');
          await page.keyboard.press('Escape');
          await page.keyboard.press('Tab');
        }
        await expect(page.locator('html')).toHaveAttribute('data-studio-theme', theme);
        const collisions = await page.getByTestId('studio-titlebar').evaluate(element => {
          const buttons = Array.from(element.querySelectorAll('button')).map(button => ({
            label: button.getAttribute('aria-label') || button.textContent?.trim(), rect: button.getBoundingClientRect(),
          })).filter(button => button.rect.width > 0 && button.rect.height > 0);
          return buttons.flatMap((button, index) => buttons.slice(index + 1).filter(other =>
            button.rect.left < other.rect.right - 1 && button.rect.right > other.rect.left + 1
            && button.rect.top < other.rect.bottom - 1 && button.rect.bottom > other.rect.top + 1,
          ).map(other => `${button.label} overlaps ${other.label}`));
        });
        expect(collisions).toEqual([]);
        expect(await page.getByTestId('studio-titlebar').evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);
        if (process.env.JOB_RESULTS_DIR) {
          mkdirSync(process.env.JOB_RESULTS_DIR, { recursive: true });
          await page.getByTestId('studio-titlebar').screenshot({ path: join(process.env.JOB_RESULTS_DIR, `develop-${state.name}-${width}-${theme}.png`) });
        }
      }
    }
  });
}
