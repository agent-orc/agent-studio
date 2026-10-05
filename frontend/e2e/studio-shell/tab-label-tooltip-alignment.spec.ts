import { expect, test } from '@playwright/test';
import path from 'node:path';

const EMPTY_GROUPED = {
  backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
  failedPickup: [], review: [], autoReview: [], humanReview: [], escalated: [],
  codeNotComplete: [], completed: [], archive: [],
};

test('restored task tab hides its watch path and uses the canonical tooltip in both themes', async ({ page }) => {
  await page.route('**/api/**', route => {
    const url = route.request().url();
    const body = url.includes('/api/v1/studio/board') ? EMPTY_GROUPED
      : url.includes('/api/v1/studio/runner/status') ? { projects: {} }
      : [];
    return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
  });
  await page.addInitScript(() => {
    const taskKey = 'C:\\Projects\\agent-taskboard-workspace\\projects\\agent-taskboard::ASS-1766';
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [
        { kind: 'hub', projectName: 'Agent Software Studio', section: 'wiki' },
        { kind: 'task', taskKey },
      ],
      activeKey: `task:${taskKey}`,
    }));
  });

  await page.goto('/');
  // The deliberately skeletal API fixture can surface an unrelated global
  // error dialog while the restored-tab projection still renders correctly.
  await page.addStyleTag({ content: 'app-error-dialog { display: none !important; }' });
  const taskTab = page.getByTestId(/studio-tab-task:.*ASS-1766/);
  await expect(taskTab).toBeVisible();
  await expect(taskTab.locator('.studio-tab__title')).toHaveText('ASS-1766');
  await expect(taskTab).not.toContainText('C:\\Projects');
  await expect(taskTab).not.toHaveAttribute('title', /.+/);

  await taskTab.focus();
  const tooltip = page.getByRole('tooltip');
  await expect(tooltip).toHaveText('ASS-1766');
  await expect(taskTab).toHaveAttribute('aria-describedby', await tooltip.getAttribute('id') ?? 'missing');

  const hubTab = page.getByTestId(/studio-tab-hub:Agent Software Studio/);
  const icon = hubTab.locator('app-studio-icon');
  const hubBox = await hubTab.boundingBox();
  const iconBox = await icon.boundingBox();
  expect(hubBox).not.toBeNull();
  expect(iconBox).not.toBeNull();
  expect(Math.abs((iconBox!.y + iconBox!.height / 2) - (hubBox!.y + hubBox!.height / 2))).toBeLessThan(1);

  const resultsDir = process.env.JOB_RESULTS_DIR;
  if (resultsDir) {
    for (const theme of ['light', 'dark'] as const) {
      await page.evaluate(value => document.documentElement.setAttribute('data-studio-theme', value), theme);
      await expect(tooltip).toBeVisible();
      await page.screenshot({ path: path.join(resultsDir, `app-tooltip-${theme}.png`) });
    }
  }
});
