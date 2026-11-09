import { test, expect, type Locator, type Page } from '@playwright/test';
import { existsSync, mkdirSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { setTheme } from '../helpers/theme';

const phase = process.env.A11Y_CAPTURE_PHASE ?? 'after';
const evidence = join(process.env.JOB_RESULTS_DIR ?? 'test-results', 'accessibility');

async function shot(page: Page, name: string, testId: string): Promise<void> {
  mkdirSync(evidence, { recursive: true });
  await page.getByTestId(testId).screenshot({ path: join(evidence, `${phase}-${name}.png`) });
}

async function assertTarget(page: Page, target: Locator): Promise<void> {
  await expect(target).toBeVisible();
  const box = await target.boundingBox();
  expect(box?.width).toBeGreaterThanOrEqual(24);
  expect(box?.height).toBeGreaterThanOrEqual(24);
  await page.keyboard.press('Tab');
  await target.focus();
  await expect(target).toHaveCSS('outline-style', 'solid');
}

async function axeAudit(page: Page): Promise<unknown> {
  const axePath = resolve('node_modules/axe-core/axe.min.js');
  if (!existsSync(axePath)) throw new Error('axe-core is required for A11Y_AXE=1');
  await page.addScriptTag({ path: axePath });
  return page.evaluate(async () => {
    const axe = (window as unknown as { axe: { run: (root: Document, options: unknown) => Promise<{
      violations: { id: string; impact: string; nodes: { target: string[]; html: string; failureSummary: string }[] }[];
    }> } }).axe;
    const result = await axe.run(document, {
      runOnly: { type: 'rule', values: ['target-size', 'nested-interactive', 'scrollable-region-focusable'] },
    });
    return result.violations.map(({ id, impact, nodes }) => ({
      id, impact,
      nodes: nodes.map(node => {
        const ancestry: string[] = [];
        let element = document.querySelector(node.target[0])?.parentElement ?? null;
        for (let depth = 0; element && depth < 6; depth += 1, element = element.parentElement) {
          ancestry.push(`${element.tagName.toLowerCase()}${element.getAttribute('data-testid') ? `[${element.getAttribute('data-testid')}]` : ''}`);
        }
        return { target: node.target, html: node.html, failureSummary: node.failureSummary, ancestry };
      }),
    }));
  });
}

async function stubApp(page: Page): Promise<void> {
  const project = {
    sourceType: 'local-folder', id: 'PROJ-A11Y', displayName: 'Example project',
    shortCode: 'A11', workspaceId: 'accessibility', color: null,
    cliDefault: null, modelDefault: null, sortOrder: 0,
    storageLocation: '/tmp/example-project', rootPath: '/tmp/example-project',
    repositoryPath: '/tmp/example-project', repositoryUrl: null, urls: [],
    archived: false, createdAt: '2026-07-23T08:00:00Z',
  };
  const workspaces = [{
    id: 'accessibility', displayName: 'Accessibility', sortOrder: 0,
    isDefault: true, color: null, createdAt: '2026-07-23T08:00:00Z',
    projects: [project],
  }];
  const board = {
    backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
    failedPickup: [], codeNotComplete: [], review: [], autoReview: [], humanReview: [],
    escalated: [], completed: [], archive: [],
  };
  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname;
    let body: unknown = [];
    if (path === '/api/v1/studio/auth/status') body = {
      profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
    };
    else if (path === '/api/watch-paths') body = [{
      name: 'Example project', path: '/tmp/example-project',
      rootPath: '/tmp/example-project', repositoryPath: '/tmp/example-project',
    }];
    else if (path === '/api/v1/projects') body = [project];
    else if (path === '/api/v1/workspaces') body = workspaces;
    else if (path === '/api/v1/studio/board') body = board;
    else if (path === '/api/v1/studio/runner/status') body = { projects: {} };
    else if (path === '/api/projects/settings') body = {};
    else if (path === '/api/tasks/archive') body = { items: [], total: 0 };
    else if (path === '/api/environment') body = {
      isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false },
    };
    else if (path === '/api/dev-tools/flags') body = {
      updateStableEnabled: false, deleteE2EJobsEnabled: false,
    };
    else if (path === '/api/git/hygiene') body = { isRepo: false, error: null };
    else if (path === '/api/cli/usage') body = { at: new Date().toISOString(), sessions: [] };
    else if (path === '/api/cli/quota') body = {
      at: new Date().toISOString(), ttlSeconds: 600, snapshots: [],
    };
    else if (path === '/api/cli/quota/model-routes') body = {
      catalogueVersion: 'audit-fixture', profiles: {}, states: {}, callersCannotReroute: [],
      routes: [{ fromCliType: 'claude', fromModel: 'claude-opus-5', fromThinkingLevel: 'high',
        toCliType: 'codex', toModel: 'gpt-5.6-sol', toThinkingLevel: 'high',
        source: 'catalogue', fromInputPerMTok: 15, fromOutputPerMTok: 75,
        toInputPerMTok: 2, toOutputPerMTok: 10, reroutable: true }],
    };
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
  });
}

for (const width of [1280, 390]) {
  for (const theme of ['light', 'dark'] as const) {
    test(`${width}px ${theme}: Explorer, tabs and CLI models`, async ({ page }) => {
      await page.setViewportSize({ width, height: 850 });
      await stubApp(page);
      await page.goto('/');
      await expect(page.getByTestId('studio-sidebar')).toBeAttached();
      // The fixture stubs unrelated API responses. Keep late error dialogs
      // from covering the audited controls or changing the screenshots.
      await page.addStyleTag({ content: '[data-testid="error-dialog-overlay"] { display: none !important; }' });
      let railStyle: Awaited<ReturnType<Page['addStyleTag']>> | null = null;
      if (width === 390) {
        // The phone shell hides Explorer; reveal its 240px rail for the
        // requested component-width capture and hit-target inspection.
        railStyle = await page.addStyleTag({ content: `
          @media (max-width: 600px) {
            .studio-sidebar { display: block !important; }
            .studio-body { grid-template-columns: var(--studio-activitybar-w) 240px minmax(0, 1fr) auto !important; }
          }
        ` });
      }
      await setTheme(page, theme);
      await shot(page, `${width}-${theme}-explorer`, 'studio-sidebar');
      if (phase === 'after') {
        await assertTarget(page, page.getByTestId('studio-explorer-add-workspace'));
        await assertTarget(page, page.locator('[data-testid^="studio-workspace-"][data-testid$="-add-project"]').first());
        await assertTarget(page, page.locator('[data-testid^="studio-explorer-ws-group-"]').first());
        const workspace = page.getByTestId('studio-explorer-ws-group-accessibility');
        if (await workspace.getAttribute('aria-expanded') === 'false') await workspace.click();
        const project = page.getByTestId('studio-explorer-project-Example project');
        await expect(project).toBeVisible();
        if (width === 1280) {
          await page.getByTestId('studio-project-picker-trigger').click();
          await page.getByTestId('studio-project-picker-item-Example project').click();
          await expect(page.getByTestId('studio-titlebar-auto-toggle')).toBeAttached();
        }
      }
      if (railStyle) await railStyle.evaluate(element => element.remove());
      await shot(page, `${width}-${theme}-tabs`, 'studio-tabbar');
      if (phase === 'after') {
        await assertTarget(page, page.locator('[data-testid^="studio-tab-close-"]').first());
        if (width === 1280) {
          const picker = page.getByTestId('studio-project-picker-trigger');
          const autoToggle = page.getByTestId('studio-titlebar-auto-toggle');
          await assertTarget(page, picker);
          await page.keyboard.press('Tab');
          await expect(autoToggle).toBeFocused();
          await expect(autoToggle).toHaveCSS('outline-style', 'solid');
          const firstTab = page.getByTestId('studio-tab-list').getByRole('tab').first();
          await firstTab.focus();
          await page.keyboard.press('Enter');
          await expect(firstTab).toHaveAttribute('aria-selected', 'true');
        }
        expect(await page.locator('button button, button [role="button"], [role="tab"] button').count()).toBe(0);
      }
      const shellAxe = phase === 'after' && process.env.A11Y_AXE === '1'
        ? await axeAudit(page) : null;

      await page.goto('/#/workspace/settings');
      await setTheme(page, theme);
      await page.addStyleTag({ content: '[data-testid="error-dialog-overlay"] { display: none !important; }' });
      const caps = page.getByTestId('workspace-settings-rail-caps');
      await expect(caps).toBeVisible();
      await caps.evaluate((element: HTMLElement) => element.click());
      const table = page.getByTestId('quota-fallback-routes');
      await expect(table).toBeVisible();
      await shot(page, `${width}-${theme}-cli-table`, 'quota-fallback-routes');

      if (phase === 'after' && process.env.A11Y_AXE === '1') {
        const tableAxe = await axeAudit(page);
        writeFileSync(join(evidence, `after-${width}-${theme}-axe.json`), JSON.stringify({
          axeCoreVersion: '4.14.0', shell: shellAxe, cliManagement: tableAxe,
        }, null, 2));
      }

      if (phase === 'before') return;
      const wrapper = page.getByTestId('cli-models-table-scroll');
      await page.keyboard.press('Tab');
      await wrapper.focus();
      await expect(wrapper).toHaveCSS('outline-style', 'solid');
      if (width === 1280) {
        expect(await wrapper.evaluate((element: HTMLElement) => {
          const event = new KeyboardEvent('keydown', { key: 'PageDown', bubbles: true, cancelable: true });
          element.dispatchEvent(event);
          return event.defaultPrevented;
        })).toBe(false);
      }
      await page.keyboard.press('End');
      const end = await wrapper.evaluate((element: HTMLElement) => element.scrollLeft);
      if (width === 390) expect(end).toBeGreaterThan(0);
      await page.keyboard.press('Home');
      await expect.poll(() => wrapper.evaluate((element: HTMLElement) => element.scrollLeft)).toBe(0);
      await page.keyboard.press('PageDown');
      if (width === 390) await expect.poll(() => wrapper.evaluate((element: HTMLElement) => element.scrollLeft)).toBeGreaterThan(0);
      await page.keyboard.press('PageUp');
      await page.keyboard.press('ArrowDown');
      await page.keyboard.press('ArrowUp');
      await page.keyboard.press('Tab');
      await expect(wrapper).not.toBeFocused();
    });
  }
}
