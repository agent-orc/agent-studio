import { expect, test } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { installRoutes } from '../helpers/ui-foundation-surfaces';

for (const theme of ['light', 'dark'] as const) {
  test(`phone settings picker follows direct open, switch and back in ${theme}`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await installRoutes(page);
    await page.addInitScript(theme => localStorage.setItem('atp.studio.theme', theme), theme);
    await page.goto('/#/workspace/settings/appearance');
    const picker = page.getByRole('combobox', { name: 'Settings section', exact: true });
    const shell = page.getByTestId('workspace-settings-inline');
    const output = process.env.JOB_RESULTS_DIR ?? testInfo.outputDir;
    mkdirSync(output, { recursive: true });
    async function agrees(section: string, title: string, step: string) {
      const heading = shell.getByRole('heading', { name: title, exact: true });
      await expect(heading).toBeVisible();
      await expect.soft(picker).toHaveValue(section);
      await expect.soft(picker.locator('option:checked')).toHaveText((await heading.textContent())!.trim());
      await page.screenshot({ path: join(output, `settings-picker-${step}-${theme}-390--mocked.png`) });
    }
    await agrees('appearance', 'Appearance', 'direct');
    await picker.selectOption('updates');
    await agrees('updates', 'Updates', 'switch');
    // Section selection replaces the route. Leave settings before going Back
    // so restoration also exercises a newly rendered picker.
    await page.goto('/#/board');
    await expect(shell).toBeHidden();
    await expect(page.getByTestId('task-card').first()).toBeVisible();
    await page.goBack();
    await agrees('updates', 'Updates', 'back');
  });
}
