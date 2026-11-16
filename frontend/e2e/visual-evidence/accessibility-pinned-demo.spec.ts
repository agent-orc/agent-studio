import { expect, test, type Page } from '@playwright/test';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { checkAccessibility } from '../helpers/accessibility';
import { setTheme, type Theme } from '../helpers/theme';

// The wrapper seeds a temporary ADR-0056 workspace. Check after webServer
// starts, before a page can navigate to the target.
function assertPinnedDemo(): void {
  const demoRoot = process.env.PW_E2E_WORKSPACE;
  if (process.env.PW_A11Y_PINNED_DEMO !== '1'
      || process.env.PW_PREBUILT !== '1'
      || process.env.PW_E2E_PROFILE !== 'demo'
      || !demoRoot
      || !fs.realpathSync(demoRoot).startsWith(path.join(os.tmpdir(), 'studio-e2e-'))
      || !fs.existsSync(path.join(demoRoot, 'projects', 'demo-app'))
      || !fs.existsSync(path.join(demoRoot, 'projects', 'demo-platform'))) {
    throw new Error('Run through scripts/presentation-capture/run-accessibility.sh with pinned demo data.');
  }
}

const CARD = '[data-testid="task-card"], [data-testid="job-card"]';
const CAPTURE = process.env.A11Y_RAIL_CAPTURE ?? 'after';
const RESULTS = process.env.JOB_RESULTS_DIR;

test.use({ reducedMotion: 'reduce', video: 'off', trace: 'off' });
test.setTimeout(120_000);
test.beforeEach(() => assertPinnedDemo());

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
  await showLane(page, 'Escalated');
  await expect(page.locator(CARD).filter({ hasText: 'DEMO-9' }).first()).toBeVisible();
  const keys = await page.locator(CARD).allTextContents();
  expect(keys.some((text) => /\b(?:AGT|PROJ)-\d+\b/.test(text))).toBe(false);
}

/** The phone layout has no inspector pane; the active task tab marks the open detail. */
async function expectTaskOpen(page: Page, width: number): Promise<void> {
  await expect(page.getByTestId('studio-tab-list').locator('[data-tab-kind="task"] [role="tab"][aria-selected="true"]'))
    .toBeVisible();
  await expect(width < 720 ? page.locator('app-mobile-pane-switcher') : page.getByTestId('pane-protocol')).toBeVisible();
}

/** The 390 px board shows one lane at a time behind the Board lane select. */
async function showLane(page: Page, lane: string): Promise<void> {
  const select = page.getByRole('combobox', { name: 'Board lane' });
  if (await select.isVisible()) await select.selectOption({ label: lane });
}

/** Review screenshot of an audited state, kept when JOB_RESULTS_DIR is set. */
async function captureState(page: Page, name: string): Promise<void> {
  if (RESULTS) await page.screenshot({ path: path.join(RESULTS, `a11y-${name}.png`), animations: 'disabled' });
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
      await captureState(page, `board-${theme}-${width}`);

      await showLane(page, 'Human review');
      const card = page.locator(CARD).filter({ hasText: 'DEMO-5' }).first();
      await card.scrollIntoViewIfNeeded();
      await card.click();
      await expectTaskOpen(page, width);
      if (width < 720) {
        await checkAccessibility(page, testInfo, `detail-${theme}-${width}`);
        await captureState(page, `detail-${theme}-${width}`);
        return;
      }
      const tabs = ['task', 'activity', 'protocol'] as const;
      for (const tab of tabs) {
        await selectInspectorTab(page, tab);
        await expect(page.getByTestId('pane-protocol-body')).toHaveAttribute('data-active-tab', tab);
        await checkAccessibility(page, testInfo, `detail-${tab}-${theme}-${width}`);
      }
      await captureState(page, `detail-${theme}-${width}`);
    });

    test(`pinned attention, escalation, Dossier and usage accessibility ${theme} ${width}`, async ({ page }, testInfo) => {
      test.skip(CAPTURE === 'before', 'Baseline run only captures the rail.');
      await page.setViewportSize({ width, height: 900 });
      await openBoard(page);
      await setTheme(page, theme);

      await page.getByTestId('studio-attention-trigger').click();
      await expect(page.getByTestId('studio-attention-list')).toBeVisible();
      await checkAccessibility(page, testInfo, `needs-you-${theme}-${width}`);
      await captureState(page, `needs-you-${theme}-${width}`);
      await page.keyboard.press('Escape');
      await expect(page.getByTestId('studio-attention-list')).toBeHidden();

      const escalated = page.locator(CARD).filter({ hasText: 'DEMO-9' }).first();
      await escalated.scrollIntoViewIfNeeded();
      await escalated.click();
      await expectTaskOpen(page, width);
      await checkAccessibility(page, testInfo, `escalated-${theme}-${width}`);
      await captureState(page, `escalated-${theme}-${width}`);

      await page.getByTestId('studio-ab-workbenches').click();
      const open = page.locator('[data-testid^="workbench-overview-open-"]').first();
      await expect(open).toBeVisible();
      await open.click();
      await expect(page.getByTestId('workbench-viewer')).toBeVisible();
      await checkAccessibility(page, testInfo, `dossier-${theme}-${width}`);
      await captureState(page, `dossier-${theme}-${width}`);

      const usage = page.getByTestId('studio-statusbar').locator('[data-usage-trigger]').first();
      await expect(usage).toBeVisible();
      await usage.click();
      await expect(page.getByTestId('usage-detail-surface')).toBeVisible();
      await checkAccessibility(page, testInfo, `usage-${theme}-${width}`);
      await captureState(page, `usage-${theme}-${width}`);
    });
  }

  test(`rail focus screenshot ${theme} ${CAPTURE}`, async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await openBoard(page);
    await setTheme(page, theme);
    await focusRail(page);
    if (CAPTURE === 'before') {
      // Reproduce the audited rail's pre-fix attributes. The visual markup and
      // focus treatment are unchanged, so this is its original appearance.
      await page.getByTestId('studio-activity-bar').evaluate((rail) => {
        for (const button of rail.querySelectorAll('button')) button.removeAttribute('aria-current');
        for (const button of rail.querySelectorAll('button[data-panel], [data-testid="studio-ab-workbenches"]')) {
          button.removeAttribute('aria-label');
        }
        for (const icon of rail.querySelectorAll('app-studio-icon')) icon.removeAttribute('aria-hidden');
      });
    }
    if (!RESULTS) throw new Error('JOB_RESULTS_DIR is required for rail screenshots.');
    await page.getByTestId('studio-activity-bar').screenshot({
      path: path.join(RESULTS, `rail-focus-${CAPTURE}-${theme}.png`),
      animations: 'disabled',
    });
  });
}
