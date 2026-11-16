import { expect, test, type Page } from '@playwright/test';
import { mkdir } from 'node:fs/promises';
import { join } from 'node:path';

type PickupMode = 'auto' | 'manual' | 'paused';
const project = 'Remote Pickup';

function grouped() {
  return {
    backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
    failedPickup: [], codeNotComplete: [], autoReview: [], humanReview: [],
    escalated: [], review: [], completed: [], archive: [],
  };
}

async function installRoutes(page: Page, initialMode: PickupMode) {
  let pickupMode = initialMode;
  const writes: PickupMode[] = [];
  const settings = () => ({
    [project]: {
      pickupMode, executionLocation: 'remote-runner-01', runnerMode: pickupMode === 'auto' ? 'auto-continuous' : pickupMode,
      buildProfilePickupAllowed: true, laneSortStrategies: {},
    },
  });
  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname;
    let body: unknown = [];
    let status = 200;
    if (path === '/api/v1/studio/board') body = grouped();
    else if (path.endsWith('/workbenches')) body = { items: [] };
    else if (path === '/api/tasks/archive') body = { items: [], total: 0 };
    else if (path === '/api/workspaces/ws-remote/quota/admission') body = {
      workspaceId: 'ws-remote',
      workspace: { status: 'ok', message: null, overrideActive: false, codingAllowed: true, hostingAllowed: true, limited: false },
      projects: [],
    };
    else if (path === '/api/watch-paths') body = [{ name: project, path: '/remote-pickup' }];
    else if (path === '/api/v1/workspaces') body = [{
      id: 'ws-remote', displayName: 'Remote workspace', sortOrder: 0, isDefault: true,
      color: null, createdAt: '2026-11-15T00:00:00Z', projects: [{
        sourceType: 'local-folder', id: 'PROJ-REMOTE', displayName: project, shortCode: 'RP',
        workspaceId: 'ws-remote', color: null, sortOrder: 0, storageLocation: '/remote-pickup',
        repositoryPath: null, rootPath: null, repositoryUrl: null, urls: [], archived: false,
        createdAt: '2026-11-15T00:00:00Z',
      }],
    }];
    else if (path === '/api/projects/settings') body = settings();
    else if (path === `/api/projects/${encodeURIComponent(project)}/execution-runner` && route.request().method() === 'PUT') {
      pickupMode = route.request().postDataJSON().pickupMode;
      writes.push(pickupMode);
      body = settings()[project];
    }
    else if (path === '/api/v1/studio/runner/status') body = { projects: {} };
    else if (path === '/api/v1/studio/auth/status') body = {
      profile: 'local', bootstrapRequired: false, authenticated: false, user: null,
    };
    else if (path === '/api/environment') body = {
      isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false },
    };
    else if (path === '/api/dev-tools/flags') body = {
      updateStableEnabled: false, deleteE2EJobsEnabled: false,
    };
    else if (path === '/api/cli/usage') body = { at: '2026-11-15T00:00:00Z', sessions: [] };
    else if (path === '/api/cli/quota') body = { at: '2026-11-15T00:00:00Z', ttlSeconds: 600, snapshots: [] };
    else if (path === '/api/usage/cockpit') { status = 503; body = {}; }
    else if (path === '/api/v1/management/credential-inventory') body = { generatedAt: '2026-11-15T00:00:00Z', items: [] };
    else if (path === '/api/git/hygiene') body = { isRepo: false, error: null };
    await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
  });
  return writes;
}

for (const initialMode of ['auto', 'manual'] as const) {
  test(`remote project ${initialMode} agrees across pickup controls and titlebar click`, async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.addInitScript(() => {
      localStorage.setItem('atp.flag.vsCodeLayout', '1');
      localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
        v: 1, tabs: [{ kind: 'board', projectName: 'Remote Pickup' }], activeKey: 'board:Remote Pickup',
      }));
    });
    const writes = await installRoutes(page, initialMode);
    await page.goto('/');
    const titlebar = page.getByTestId('studio-titlebar-auto-toggle');
    const explorer = page.getByTestId(`studio-explorer-project-auto-pickup-${project}`);
    const lane = page.getByTestId('lane-auto-toggle-3-progress');
    await expect(titlebar).toHaveText(initialMode === 'auto' ? 'Auto' : 'Manual');
    await expect(titlebar).toHaveAttribute('aria-label', initialMode === 'auto'
      ? `Auto-pickup is on for ${project} — click to pause.`
      : `Auto-pickup is paused for ${project} — click to enable.`);
    await expect(explorer).toHaveAttribute('aria-label', initialMode === 'auto' ? 'Auto-pickup active' : 'Auto-pickup manual');
    await expect(lane).toHaveText(initialMode === 'auto' ? 'AUTO' : 'MANUAL');

    await titlebar.click();
    const nextMode = initialMode === 'auto' ? 'paused' : 'auto';
    await expect.poll(() => writes).toEqual([nextMode]);
    await expect(titlebar).toHaveText(nextMode === 'auto' ? 'Auto' : 'Paused');
    await expect(explorer).toHaveAttribute('aria-label', nextMode === 'auto' ? 'Auto-pickup active' : 'Auto-pickup paused');
    await expect(lane).toHaveText(nextMode === 'auto' ? 'AUTO' : 'PAUSED');
    const results = process.env.JOB_RESULTS_DIR;
    if (results) {
      await mkdir(join(results, 'screenshots'), { recursive: true });
      await page.screenshot({ path: join(results, 'screenshots', `remote-pickup-${initialMode}.png`) });
      await page.evaluate(() => {
        document.documentElement.dataset['studioTheme'] = 'dark';
        localStorage.setItem('atp.studio.theme', 'dark');
      });
      await page.screenshot({ path: join(results, 'screenshots', `remote-pickup-${initialMode}-dark.png`) });
    }
    await page.goto('/#/projects/remote-pickup/settings');
    const card = page.getByTestId('project-execution-card');
    await expect(card.getByTestId(`project-pickup-mode-${nextMode}`)).toHaveAttribute('aria-pressed', 'true');
  });
}
