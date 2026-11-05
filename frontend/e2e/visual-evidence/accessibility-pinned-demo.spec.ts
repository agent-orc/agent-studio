import { expect, test, type Page } from '@playwright/test';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { checkAccessibility } from '../helpers/accessibility';
import { setTheme, type Theme } from '../helpers/theme';

// The wrapper seeds a temporary ADR-0056 workspace. Refuse any other target.
const demoRoot = process.env.TaskRepository;
if (process.env.PW_A11Y_PINNED_DEMO !== '1'
    || !demoRoot
    || !fs.realpathSync(demoRoot).startsWith(path.join(os.tmpdir(), 'atp-worktree-test-'))
    || !fs.existsSync(path.join(demoRoot, 'projects', 'demo-app'))
    || !fs.existsSync(path.join(demoRoot, 'projects', 'demo-platform'))) {
  throw new Error('Run through scripts/presentation-capture/run-accessibility.sh with pinned demo data.');
}

const CARD = '[data-testid="task-card"], [data-testid="job-card"]';
const CAPTURE = process.env.A11Y_RAIL_CAPTURE ?? 'after';
const RESULTS = process.env.JOB_RESULTS_DIR;

test.use({ reducedMotion: 'reduce', video: 'off', trace: 'off' });
test.setTimeout(120_000);

async function openBoard(page: Page): Promise<void> {
  await page.goto('/');
  await expect(page.getByTestId('studio-activity-bar')).toBeVisible({ timeout: 30_000 });
  for (let attempt = 0; attempt < 8; attempt++) {
    const dismiss = page.getByTestId('crash-recovery-dismiss').first();
    if (await dismiss.isVisible().catch(() => false)) {
      await dismiss.click({ force: true });
    }
    await page.waitForTimeout(250);
  }
  const picker = page.getByTestId('studio-project-picker-trigger');
  if (await picker.isVisible()) {
    await picker.click();
    const panel = page.getByTestId('studio-project-picker-panel');
    await expect(panel.getByText('Demo App', { exact: true })).toBeVisible();
    await expect(panel.getByText('Demo Platform', { exact: true })).toBeVisible();
    await panel.getByText('Demo App', { exact: true }).click();
  }
  await expect(page.locator(CARD).filter({ hasText: 'DEMO-9' }).first()).toBeVisible();
  const keys = await page.locator(CARD).allTextContents();
  expect(keys.some((text) => /\b(?:AGT|PROJ)-\d+\b/.test(text))).toBe(false);
}

async function focusRail(page: Page): Promise<void> {
  await page.getByTestId('studio-ab-explorer').focus();
  await page.keyboard.press('Tab');
  const filters = page.getByTestId('studio-ab-filters');
  await expect(filters).toBeFocused();
  await expect.poll(() => filters.evaluate((element) => element.matches(':focus-visible'))).toBe(true);
}

async function selectInspectorTab(page: Page, tab: 'task' | 'activity' | 'protocol'): Promise<void> {
  const label = { task: 'Task', activity: 'Activity', protocol: 'Result' }[tab];
  const tabs = page.getByRole('tablist', { name: 'Task / Activity / Result' });
  await expect(tabs).toBeVisible();
  const inline = tabs.getByRole('tab', { name: label, exact: true });
  if (await inline.isVisible()) {
    await inline.dispatchEvent('click');
    return;
  }
  await tabs.getByRole('button', { name: /More tabs/ }).dispatchEvent('click');
  await page.getByRole('menu', { name: 'More tabs' }).getByRole('menuitem', { name: label }).dispatchEvent('click');
}

for (const theme of ['light', 'dark'] as const satisfies readonly Theme[]) {
  for (const width of [1280, 390]) {
    test(`pinned board and detail accessibility ${theme} ${width}`, async ({ page }, testInfo) => {
      test.skip(CAPTURE === 'before', 'Baseline run only captures the rail.');
      await page.setViewportSize({ width, height: 900 });
      await openBoard(page);
      await setTheme(page, theme);
      await checkAccessibility(page, testInfo, `board-${theme}-${width}`);

      const card = page.locator(CARD).filter({ hasText: 'DEMO-5' }).first();
      await card.scrollIntoViewIfNeeded();
      await card.click();
      await expect(page.getByTestId('pane-protocol')).toBeVisible();
      const tabs = ['task', 'activity', 'protocol'] as const;
      for (const tab of tabs) {
        await selectInspectorTab(page, tab);
        await expect(page.getByTestId('pane-protocol-body')).toHaveAttribute('data-active-tab', tab);
        await checkAccessibility(page, testInfo, `detail-${tab}-${theme}-${width}`);
      }
    });
  }

  test(`rail focus screenshot ${theme} ${CAPTURE}`, async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await openBoard(page);
    await setTheme(page, theme);
    await focusRail(page);
    if (!RESULTS) throw new Error('JOB_RESULTS_DIR is required for rail screenshots.');
    await page.getByTestId('studio-activity-bar').screenshot({
      path: path.join(RESULTS, `rail-focus-${CAPTURE}-${theme}.png`),
      animations: 'disabled',
    });
  });
}
