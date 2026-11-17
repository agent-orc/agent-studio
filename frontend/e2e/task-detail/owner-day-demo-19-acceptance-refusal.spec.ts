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
  { theme: 'dark', width: 390 },
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
        // AGT-3830: no plain Accept to be refused. The refusal's next step
        // leads in the toolbar, with the reason beside it; a refused click
        // is covered by board/owner-day-refused-acceptance.spec.ts.
        const hold = page.getByTestId('studio-triage-panel').getByTestId('acceptance-refusal');
        await expect(hold).toHaveAttribute('data-refusal-source', 'readiness', { timeout: 15_000 });
        await expect(hold).toHaveAttribute('data-replaces-accept', 'true');
        await expect(hold).toContainText('requirement-fit');
        await expect(page.getByTestId('studio-triage-action-mark-done')).toHaveCount(0);
        await hold.getByTestId('acceptance-refusal-action').hover();
        await expect(page.getByTestId('cac-tooltip')).toContainText('content review again');
        await shot(page, `demo-19-toolbar-next-step-${tag}`);
        await page.mouse.move(0, 0);
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
