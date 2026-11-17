import { test, expect, type Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { dismissDevErrorDialog, setTheme, type Theme } from '../helpers/theme';

// AGT-3867, F10. Runs only with PW_E2E_PROFILE=owner-day: the real backend
// seeds DEMO-19, an integrated card whose requirement-fit review returned
// concerns on the current brief. No route is mocked.
const RESULTS = join(process.env.JOB_RESULTS_DIR ?? 'test-results', 'owner-day-demo-19-acceptance-refusal');
const VARIANTS: readonly { theme: Theme; width: number }[] = [
  { theme: 'light', width: 1280 },
  { theme: 'light', width: 390 },
  { theme: 'dark', width: 1280 },
];

async function openDemo19(page: Page, theme: Theme): Promise<void> {
  await page.addInitScript((value) => localStorage.setItem('atp.studio.theme', value), theme);
  await page.goto('/#/tasks/DEMO-19');
  await dismissDevErrorDialog(page);
  await setTheme(page, theme);
  await expect(page.getByTestId('task-detail-load-error')).toHaveCount(0);
}

async function shot(page: Page, name: string): Promise<void> {
  mkdirSync(RESULTS, { recursive: true });
  await page.screenshot({ path: join(RESULTS, `${name}.png`) });
}

test.describe('DEMO-19 acceptance refusal in the card detail (AGT-3867)', () => {
  test.describe.configure({ mode: 'serial', retries: 0 });
  test.skip(() => process.env.PW_E2E_PROFILE !== 'owner-day', 'Requires the isolated owner-day profile.');

  for (const { theme, width } of VARIANTS) {
    test(`offers the board's next steps and names the server reason (${theme} ${width})`, async ({ page }) => {
      test.setTimeout(120_000);
      await page.setViewportSize({ width, height: width < 600 ? 844 : 900 });
      await openDemo19(page, theme);
      const tag = `${theme}-${width}`;

      // The detail offers what the board card offers, for a concern on the current brief.
      const steps = page.getByTestId('acceptance-next-steps');
      await expect(steps).toHaveAttribute('data-readiness', 'review-concerns', { timeout: 30_000 });
      await expect(page.getByTestId('acceptance-next-steps-reason')).toContainText('Accept will be refused.');
      await expect(page.getByTestId('stale-brief-acceptance')).toHaveCount(0);
      await expect(steps.getByTestId('acceptance-next-steps-send-round')).toBeVisible();
      await expect(steps.getByTestId('review-current-brief')).toBeVisible();
      await expect(steps.getByTestId('accept-with-reason-open')).toBeVisible();
      await steps.scrollIntoViewIfNeeded();
      await shot(page, `demo-19-next-steps-${tag}`);

      if (width >= 1024) {
        // Before the click, Accept says why it will be refused.
        const accept = page.getByTestId('studio-triage-action-mark-done');
        await expect(accept).toBeVisible({ timeout: 15_000 });
        const hold = page.getByTestId('acceptance-refusal');
        await expect(hold).toHaveAttribute('data-refusal-source', 'hold');
        await expect(hold).toContainText('requirement-fit');
        await accept.hover();
        await expect(page.getByTestId('cac-tooltip')).toContainText('Accept will be refused.');
        await shot(page, `demo-19-accept-hover-${tag}`);

        if (theme === 'light') {
          // The refused click shows the server's reason, never a brief mismatch.
          const refused = page.waitForResponse(response =>
            response.url().includes('/move') && response.request().method() === 'POST');
          await page.mouse.move(0, 0);
          await accept.click();
          const confirm = page.getByTestId('confirm-dialog-confirm');
          if (await confirm.isVisible({ timeout: 1_000 }).catch(() => false)) await confirm.click();
          const response = await refused;
          expect(response.status()).toBe(409);
          const body = await response.json() as { code?: string; error?: string };
          expect(body.code).toBe('completion-content-gap');
          const reason = (body.error ?? '').replace(/^Completion refused:\s*/, '');
          // AGT-3873: the refusal toast docks below the toolbar with its actions.
          const toast = page.getByTestId('notification-stack-below-toolbar');
          await expect(toast).toContainText(`Acceptance refused: ${reason}`);
          await expect(toast).not.toContainText('does not match the current brief');
          const banner = page.getByTestId('task-move-refusal');
          await expect(banner).toContainText(`Acceptance refused: ${reason}`);
          await expect(banner).toContainText('Acceptance next steps');
          await expect(page.getByTestId('acceptance-refusal')).toHaveAttribute('data-refusal-source', 'server');
          await expect(page.getByTestId('acceptance-refusal-action')).toHaveText('Accept with override…');
          await expect(toast.getByTestId('acceptance-refusal-toast-override')).toBeVisible();
          await expect(banner.getByTestId('task-move-refusal-override')).toBeVisible();
          // The refused Accept opened the written-reason form in place, focused.
          await expect(page.getByTestId('acceptance-refusal-override-reason')).toBeFocused();
          await shot(page, `demo-19-refused-click-${tag}`);
          await page.getByTestId('acceptance-refusal-cancel').click();
        }
      }

      // The round opens prefilled from the detail; it is not sent here.
      await steps.getByTestId('acceptance-next-steps-send-round').click();
      const dialog = page.getByTestId('acceptance-round-dialog');
      await expect(dialog).toBeVisible();
      await expect(page.getByTestId('acceptance-round-prompt')).toHaveValue(/Close the acceptance gap on this card/);
      await shot(page, `demo-19-send-round-dialog-${tag}`);
      await page.getByTestId('acceptance-round-cancel').click();
      await expect(dialog).toHaveCount(0);
    });
  }
});
