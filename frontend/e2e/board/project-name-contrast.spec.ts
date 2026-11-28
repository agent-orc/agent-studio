import { expect, test } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { PREVIEW_TOKEN_SETS } from '../../src/app/features/studio-shell/services/preview-token-sets';
import { installRoutes } from '../helpers/ui-foundation-surfaces';
import { sampleColours } from '../helpers/theme';
import { contrastRatio } from '../helpers/contrast';

const sets = ['studio-default', ...PREVIEW_TOKEN_SETS.map(set => set.id)];

for (const theme of ['light', 'dark'] as const) {
  test(`board project name contrast in every token set, ${theme}`, async ({ page }, testInfo) => {
    test.setTimeout(120_000);
    await installRoutes(page);
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await page.addInitScript(theme => {
      localStorage.setItem('atp.studio.theme', theme);
      localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
        v: 1, tabs: [{ kind: 'board', projectName: '__all__' }], activeKey: 'board:__all__',
      }));
    }, theme);
    const output = process.env.JOB_RESULTS_DIR ?? testInfo.outputDir;
    mkdirSync(output, { recursive: true });
    const measurements = [];
    for (const id of sets) {
      await page.goto(`/?tokenSet=${id}`);
      await expect(page.locator('html')).toHaveAttribute('data-studio-token-set', `${id}.${theme}`);
      const label = page.getByTestId('task-card-project-name').first();
      await expect(label).toBeVisible();
      const colours = await sampleColours(page, '[data-testid="task-card-project-name"]');
      const ratio = contrastRatio(colours.color, colours.bg);
      measurements.push({ id, theme, ...colours, ratio });
      await label.locator('..').screenshot({ path: join(output, `project-name-${id}-${theme}--mocked.png`) });
      expect.soft(ratio, `${id}.${theme}: ${colours.color} on ${colours.bg}`).toBeGreaterThanOrEqual(4.5);
      // The name follows semantic ink rather than inheriting the project hue.
      expect.soft(await label.evaluate(el => {
        const probe = document.createElement('span');
        probe.style.color = 'var(--studio-fg)';
        el.appendChild(probe);
        const semantic = getComputedStyle(probe).color;
        probe.remove();
        return getComputedStyle(el).color === semantic;
      })).toBe(true);
    }
    writeFileSync(join(output, `project-name-contrast-${theme}.json`), JSON.stringify(measurements, null, 2));
  });
}
