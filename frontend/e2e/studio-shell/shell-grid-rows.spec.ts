import { expect, test } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { setTheme } from '../helpers/theme';

const screenshots = join(process.env.JOB_RESULTS_DIR ?? 'test-results', 'shell-grid');

async function expectShellRows(page: import('@playwright/test').Page, banner: boolean): Promise<void> {
  const [titlebar, body, footer, quota] = await Promise.all([
    page.getByTestId('studio-titlebar').boundingBox(),
    page.getByTestId('studio-body').boundingBox(),
    page.getByTestId('studio-statusbar').boundingBox(),
    banner ? page.getByTestId('workspace-quota-status').boundingBox() : Promise.resolve(null),
  ]);
  expect(titlebar).not.toBeNull();
  expect(body).not.toBeNull();
  expect(footer).not.toBeNull();
  if (banner) {
    expect(quota).not.toBeNull();
    expect(quota!.y, 'quota follows titlebar').toBeCloseTo(titlebar!.y + titlebar!.height, 0);
    expect(quota!.height, 'quota uses only its content height').toBeLessThan(80);
  }
  expect(body!.y, 'body follows header content').toBeCloseTo(
    banner ? quota!.y + quota!.height : titlebar!.y + titlebar!.height, 0);
  expect(body!.height, 'body fills the available viewport').toBeGreaterThan(600);
  expect(body!.y + body!.height, 'body bottom meets footer').toBeCloseTo(footer!.y, 0);
}

async function expectTallContentKeepsShellRows(page: import('@playwright/test').Page, banner: boolean): Promise<void> {
  const main = page.getByTestId('studio-main');
  await main.evaluate(element => {
    const tall = document.createElement('div');
    tall.dataset['testid'] = 'shell-tall-content';
    tall.style.height = '1600px';
    tall.style.flex = 'none';
    element.append(tall);
  });
  await expect.poll(() => main.evaluate(element => element.scrollHeight > element.clientHeight)).toBe(true);
  await expectShellRows(page, banner);
  await page.getByTestId('shell-tall-content').evaluate(element => element.remove());
}

test('the short studio body starts below the titlebar and ends above the footer', async ({ page }) => {
  await page.addInitScript(() => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
    v: 1, tabs: [{ kind: 'workbenches', projectName: null }], activeKey: 'workbenches:__all__',
  })));
  await page.route('**/api/workbenches*', route => route.fulfill({ json: {
    projectName: null, count: 0, currentCount: 0, historyCount: 0, items: [],
  } }));
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/');
  await expect(page.getByTestId('workbench-overview-empty')).toBeVisible();
  for (const width of [390, 1280]) {
    await page.setViewportSize({ width, height: 844 });
    await expectShellRows(page, false);
    await expectTallContentKeepsShellRows(page, false);
  }
});

test('in-flow skip links cannot move the body below the quota banner', async ({ page }) => {
  await page.route('**/api/workspaces/*/quota/admission', route => {
    const workspaceId = new URL(route.request().url()).pathname.split('/')[3];
    return route.fulfill({ json: {
      workspaceId,
      workspace: {
        projectId: null, project: null, status: 'warning', message: 'Workspace quota warning',
        overrideActive: false, codingAllowed: true, hostingAllowed: true, limited: true,
      },
      projects: [],
    } });
  });
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/');
  const banner = page.getByTestId('workspace-quota-status');
  await expect(banner).toBeVisible();

  // Recreate the in-flow skip-link condition from the playthrough. The shell
  // must own its row placement even when this zero-height nav participates.
  await page.addStyleTag({ content:
    '[data-testid="studio-skip-links"] { position: static !important; }',
  });
  for (const width of [390, 1280]) {
    await page.setViewportSize({ width, height: 844 });
    await expectShellRows(page, true);
    await expectTallContentKeepsShellRows(page, true);
  }
  await page.addStyleTag({ content: 'app-workspace-quota-banner { display: none !important; }' });
  for (const width of [390, 1280]) {
    await page.setViewportSize({ width, height: 844 });
    await expectShellRows(page, false);
    await expectTallContentKeepsShellRows(page, false);
  }
});

test('Dossiers Review here stays above the phone footer and opens the Dossier', async ({ page }) => {
  const projectName = 'Layout Fixture';
  const id = 'layout-decision';
  await page.addInitScript(() => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
    v: 1, tabs: [{ kind: 'workbenches', projectName: null }], activeKey: 'workbenches:__all__',
  })));
  await page.route('**/api/workbenches*', route => route.fulfill({ json: {
    projectName: null, count: 1, currentCount: 1, historyCount: 0,
    items: [{ projectName, workbench: {
      id, key: 'LAY-D1', title: 'Layout decision', summary: 'Choose the shell grid rows.',
      status: 'decision-pending', phase: 'decision-ready', updatedAtUtc: '2026-11-16T10:00:00Z',
      entryPath: 'docs/concepts/layout-decision.md', valid: true, error: null,
      sourceTaskKeys: [], openDecisionCount: 1,
    } }],
  } }));
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/');
  const action = page.getByTestId(`workbench-overview-open-${projectName}-${id}`);
  await expect(action).toBeVisible();
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await expectShellRows(page, false);
    const [button, footer] = await Promise.all([
      action.boundingBox(), page.getByTestId('studio-statusbar').boundingBox(),
    ]);
    expect(button).not.toBeNull();
    expect(footer).not.toBeNull();
    expect(button!.y + button!.height, 'Review here is above the footer').toBeLessThan(footer!.y);
    mkdirSync(screenshots, { recursive: true });
    await page.screenshot({ path: join(screenshots, `dossiers-390-${theme}.png`), animations: 'disabled' });
  }
  await action.click();
  await expect(page.getByTestId(`studio-tab-workbench:${projectName}:${id}`)).toBeVisible();
});
