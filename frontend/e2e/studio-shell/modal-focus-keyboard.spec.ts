import { test, expect, type Locator, type Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { api } from '../helpers/api';
import { createJob, listJobs } from '../helpers/jobs';
import { SNAPSHOT } from '../../src/mockups/usage-chips/app/usage-chips.fixtures';

/**
 * AGT-3831 (playthrough AGT-3796 F4): keyboard focus in the shared modals and
 * menus at 1280 px.
 *
 * - Needs you, Add task and the escalation confirmation keep Tab and
 *   Shift+Tab inside, have an accessible name, and return focus to the opener.
 * - The project picker and the card overflow menu return focus to their
 *   trigger on Escape and on selection.
 * - The usage sheet is a named dialog that closes with Escape above an open
 *   card and returns focus to the footer chip.
 *
 * Focus never lands on the body after a close.
 *
 * With a card open, controls are activated with Space: the task detail still
 * binds Enter at document level to the card's primary action (AGT-3796 F1,
 * carded separately), which would mask the focus behaviour under test.
 */

const SHOT_DIR = join(process.env.JOB_RESULTS_DIR ?? 'test-results', 'modal-focus-keyboard');

interface WatchPath { path: string; name?: string }

async function shot(page: Page, name: string): Promise<void> {
  mkdirSync(SHOT_DIR, { recursive: true });
  await page.screenshot({ path: join(SHOT_DIR, `${name}.png`), animations: 'disabled' });
}

async function expectFocusOffBody(page: Page, step: string): Promise<void> {
  await expect.poll(() => page.evaluate(() => !!document.activeElement && document.activeElement !== document.body),
    { message: `${step}: focus is on the body` }).toBe(true);
}

async function expectFocused(page: Page, target: Locator, step: string): Promise<void> {
  await expectFocusOffBody(page, step);
  await expect(target, step).toBeFocused();
}

/** Visible tab stops inside the surface, without the focus-trap anchors. */
async function tabStops(surface: Locator): Promise<number> {
  return surface.evaluate(root => [...root.querySelectorAll<HTMLElement>(
    'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])',
  )].filter(el => el.getClientRects().length > 0 && !el.classList.contains('cdk-focus-trap-anchor')).length);
}

/** Presses Tab (or Shift+Tab) past one full cycle and asserts focus never leaves the surface. */
async function expectTabContained(page: Page, surface: Locator, step: string): Promise<void> {
  const presses = (await tabStops(surface)) + 2;
  for (const key of ['Tab', 'Shift+Tab']) {
    for (let index = 1; index <= presses; index++) {
      await page.keyboard.press(key);
      const inside = await surface.evaluate(root => root.contains(document.activeElement));
      expect(inside, `${step}: ${key} ${index} of ${presses} left the dialog`).toBe(true);
    }
  }
}

async function gotoStudio(page: Page): Promise<void> {
  await page.goto('/');
  await expect(page.getByTestId('studio-attention-trigger')).toBeVisible({ timeout: 30_000 });
}

test.describe('Modal and menu keyboard focus at 1280 px (AGT-3831)', () => {
  test.use({ viewport: { width: 1280, height: 800 } });

  let watch: WatchPath;
  let escalated: { id: string };
  const suffix = Date.now().toString(36);
  const createdTitle = `Keyboard created ${suffix}`;

  test.beforeAll(async () => {
    [watch] = await api<WatchPath[]>('/api/watch-paths');
    expect(watch, 'the isolated stack has a project').toBeTruthy();
    escalated = await createJob({
      title: `Keyboard escalation ${suffix}`, watchPath: watch.path, targetState: '5e-escalated', fixture: false,
    });
  });

  test.afterAll(async () => {
    const created = (await listJobs().catch(() => [])).filter(job => job.title === createdTitle);
    for (const job of [escalated, ...created]) {
      if (!job) continue;
      await api(`/api/tasks/${encodeURIComponent(job.id)}?watchPath=${encodeURIComponent(watch.path)}`,
        { method: 'DELETE' }).catch(() => undefined);
    }
  });

  async function openEscalatedCard(page: Page): Promise<void> {
    await page.goto(`/?job=${encodeURIComponent(escalated.id)}&watchPath=${encodeURIComponent(watch.path)}`);
    await expect(page.getByTestId('escalation-action-reissue-escalated')).toBeVisible({ timeout: 30_000 });
  }

  test('Needs you keeps Tab inside and returns focus to its trigger', async ({ page }) => {
    const observedAt = new Date(Date.now() - 5 * 60_000).toISOString();
    const items = ['AGT-9101', 'AGT-9102', 'AGT-9103'].map((cardKey, index) => ({
      id: String(index + 1), type: 'escalation.opened', project: 'alpha', cardKey, cards: [cardKey],
      reason: `Gate failed on ${cardKey}.`, observedAt, suggestedOperations: [],
    }));
    await page.route('**/api/operator/attention', route => route.fulfill({ json: { items, count: items.length } }));
    await gotoStudio(page);
    const trigger = page.getByTestId('studio-attention-trigger');
    // The live stack adds its own facts; the mocked rows are the floor.
    await expect.poll(async () => Number(await page.getByTestId('studio-attention-count').textContent()))
      .toBeGreaterThanOrEqual(items.length);
    await trigger.focus();
    await page.keyboard.press('Enter');
    const dialog = page.getByRole('dialog', { name: 'Needs you' });
    await expect(dialog).toBeVisible();
    await expect(dialog).toHaveAttribute('aria-modal', 'true');
    await expect(dialog.getByTestId('attention-row').first()).toBeVisible();
    await expectTabContained(page, dialog, 'Needs you');
    await shot(page, 'needs-you-tab-contained');

    await page.keyboard.press('Escape');
    await expect(dialog).toBeHidden();
    await expectFocused(page, trigger, 'Needs you Escape');

    await page.keyboard.press('Enter');
    await expect(dialog).toBeVisible();
    await dialog.getByTestId('studio-attention-list-close').focus();
    await page.keyboard.press('Enter');
    await expect(dialog).toBeHidden();
    await expectFocused(page, trigger, 'Needs you close button');
  });

  test('Add task is named, keeps Tab inside, and returns focus on Escape and after creating', async ({ page }) => {
    await gotoStudio(page);
    const opener = page.getByRole('button', { name: /add task/i }).first();
    await opener.focus();
    await page.keyboard.press('Enter');
    const dialog = page.getByRole('dialog', { name: /task/i });
    await expect(dialog).toBeVisible();
    await expect(dialog).toHaveAttribute('aria-labelledby', 'create-dialog-title');
    await expect.poll(() => dialog.evaluate(root => root.contains(document.activeElement))).toBe(true);
    await expectTabContained(page, dialog, 'Add task');
    await shot(page, 'add-task-tab-contained');

    await page.keyboard.press('Escape');
    await expect(dialog).toBeHidden();
    await expectFocused(page, opener, 'Add task Escape');

    await page.keyboard.press('Enter');
    await expect(dialog).toBeVisible();
    await dialog.getByTestId('create-title').fill(createdTitle);
    await dialog.locator('textarea').first().fill('Created from the keyboard focus spec.');
    await dialog.getByTestId('create-submit').focus();
    await page.keyboard.press('Enter');
    await expect(dialog).toBeHidden({ timeout: 30_000 });
    await expectFocusOffBody(page, 'Add task after create');
  });

  test('escalation confirmation keeps Tab inside and Escape returns to Continue without closing the card', async ({ page }) => {
    await openEscalatedCard(page);
    const opener = page.getByTestId('escalation-action-reissue-escalated');
    await opener.focus();
    await page.keyboard.press('Space');
    const dialog = page.getByRole('dialog', { name: 'Continue reissue' });
    await expect(dialog).toBeVisible();
    await expectTabContained(page, dialog, 'Escalation confirmation');
    await shot(page, 'escalation-confirm-tab-contained');

    await page.keyboard.press('Escape');
    await expect(dialog).toBeHidden();
    await expect(opener, 'the card stays open under the confirmation').toBeVisible();
    await expectFocused(page, opener, 'Escalation Escape');

    await page.keyboard.press('Space');
    await expect(dialog).toBeVisible();
    await dialog.getByTestId('escalation-confirm-cancel').focus();
    await page.keyboard.press('Space');
    await expect(dialog).toBeHidden();
    await expectFocused(page, opener, 'Escalation Cancel');
  });

  test('project picker returns focus to its trigger on Escape and on selection', async ({ page }) => {
    await gotoStudio(page);
    const trigger = page.getByTestId('studio-project-picker-trigger');
    await trigger.focus();
    await page.keyboard.press('Enter');
    const menu = page.getByRole('menu', { name: 'Project picker' });
    await expect(menu).toBeVisible();
    await expect.poll(() => menu.evaluate(root => root.contains(document.activeElement))).toBe(true);
    await page.keyboard.press('Escape');
    await expect(menu).toBeHidden();
    await expectFocused(page, trigger, 'Project picker Escape');

    await page.keyboard.press('Enter');
    await expect(menu).toBeVisible();
    await page.keyboard.press('Enter');
    await expect(menu).toBeHidden();
    await expectFocused(page, trigger, 'Project picker selection');
  });

  test('card overflow menu returns focus to its trigger on Escape', async ({ page }) => {
    await openEscalatedCard(page);
    const trigger = page.getByTestId('studio-triage-overflow-btn');
    await trigger.focus();
    await page.keyboard.press('Space');
    const menu = page.getByRole('menu', { name: 'Lane actions' });
    await expect(menu).toBeVisible();
    await expect.poll(() => menu.evaluate(root => root.contains(document.activeElement))).toBe(true);
    await page.keyboard.press('Escape');
    await expect(menu).toBeHidden();
    await expect(page.getByTestId('escalation-action-reissue-escalated'), 'Escape closes only the menu').toBeVisible();
    await expectFocused(page, trigger, 'Card menu Escape');
  });

  test('usage sheet is a named dialog that Escape closes above an open card, back to the footer chip', async ({ page }) => {
    await page.route('**/api/usage/cockpit**', route => route.fulfill({ json: structuredClone(SNAPSHOT) }));
    await openEscalatedCard(page);
    const chip = page.getByTestId('status-bar').getByTestId('footer-usage-claude');
    await expect(chip).toBeVisible({ timeout: 30_000 });
    await chip.focus();
    await page.keyboard.press('Space');
    const sheet = page.getByTestId('usage-detail-surface');
    await expect(sheet).toBeVisible();
    await expect(page.getByRole('dialog', { name: /usage/i })).toBeVisible();
    await expect.poll(() => sheet.evaluate(root => root.contains(document.activeElement))).toBe(true);
    await shot(page, 'usage-sheet-open');

    await page.keyboard.press('Escape');
    await expect(sheet).toBeHidden();
    await expect(page.getByTestId('escalation-action-reissue-escalated'), 'Escape closes only the sheet').toBeVisible();
    await expectFocused(page, chip, 'Usage sheet Escape');
    // An explicit role for attribute-based tools; the native element already maps to dialog.
    await expect(sheet).toHaveAttribute('role', 'dialog');
  });
});
