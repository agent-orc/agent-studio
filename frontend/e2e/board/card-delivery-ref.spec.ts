import { expect, test, type Page, type Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

const PROJECT = 'Delivery ref fixture';
const WATCH_PATH = '/fixtures/delivery-ref-card';
const DELIVERY_REF = 'runner/agent-runner-01/AGT-2220';
const RESULTS = join(process.cwd(), 'results', 'AGT-2434');

const remoteReviewTask = {
  id: 'out-of-band-nur-mit-verifizierten-commits',
  taskKey: `${WATCH_PATH}::out-of-band-nur-mit-verifizierten-commits`,
  key: 'AGT-2220',
  title: 'Out-of-band nur mit verifizierten Commits',
  state: '5-human-review',
  order: 1,
  agent: 'codex',
  cliType: 'codex',
  createdAt: '2026-07-22T15:00:00Z',
  lastActivity: '2026-07-30T00:07:44Z',
  watchPath: WATCH_PATH,
  projectName: PROJECT,
  folderPath: `${WATCH_PATH}/tasks/AGT-2220`,
  model: 'gpt-5.6-codex',
  execution: null,
  commit: null,
  commits: [],
  codeActivityDetected: false,
  ownerClientId: 'local-default',
  tags: [],
  provenance: {
    branch: 'task/out-of-band-nur-mit-verifizierten-commits',
    base: null,
    transitions: [{
      lane: '5-human-review',
      atUtc: '2026-07-29T22:07:44Z',
      branchTip: null,
      workBranchHead: '9af1a848e3be1401340b3b15c9704ae1f87c9408',
    }],
    merge: null,
  },
  integration: {
    status: 'pending',
    deliveryRef: DELIVERY_REF,
    sha: null,
    integrationBranch: 'main',
    detail: `Delivery ref '${DELIVERY_REF}' is not yet integrated into main.`,
  },
};

function json(route: Route, body: unknown) {
  return route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(body),
  });
}

async function installRoutes(page: Page): Promise<void> {
  await page.route('**/api/**', route => {
    const url = route.request().url();
    if (url.includes('/api/v1/studio/auth/status')) {
      return json(route, {
        profile: 'local',
        bootstrapRequired: false,
        authenticated: true,
        user: null,
      });
    }
    if (url.includes('/api/v1/studio/board')) {
      return json(route, {
        backlog: [],
        preparation: [],
        orchestratorPrep: [],
        ready: [],
        progress: [],
        failedPickup: [],
        codeNotComplete: [],
        review: [],
        autoReview: [],
        humanReview: [remoteReviewTask],
        escalated: [],
        completed: [],
        archive: [],
      });
    }
    if (/\/api\/(?:tasks|jobs)(\?|$)/.test(url)) return json(route, [remoteReviewTask]);
    if (url.includes('/api/watch-paths')) {
      return json(route, [{
        name: PROJECT,
        path: WATCH_PATH,
        rootPath: WATCH_PATH,
        repositoryPath: WATCH_PATH,
      }]);
    }
    if (url.includes('/api/environment')) return json(route, { isDev: false, devTools: {} });
    if (url.includes('/api/v1/studio/runner/status')) return json(route, { projects: {} });
    if (url.includes('/api/clients')) {
      return json(route, [{ id: 'local-default', displayName: 'Local', kind: 'agent-instance' }]);
    }
    if (url.includes('/api/cli/')) return json(route, { snapshots: [], sessions: [] });
    if (url.includes('/api/tasks/archive')) return json(route, { items: [], total: 0 });
    return json(route, []);
  });
}

async function boot(page: Page): Promise<void> {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.addInitScript(() => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'board', projectName: '__all__' }],
      activeKey: 'board:__all__',
    }));
  });
  await installRoutes(page);
  await page.goto('/?includeFixtures=true');
  await dismissDevErrorDialog(page);
}

test.describe('Review card delivery ref projection', () => {
  for (const theme of ['light', 'dark'] as const) {
    test(`shows the remote delivery ref from card truth (${theme})`, async ({ page }, testInfo) => {
      await boot(page);
      await setTheme(page, theme);

      const card = page.getByTestId('task-card').filter({ hasText: 'AGT-2220' }).first();
      await expect(card).toBeVisible({ timeout: 15_000 });

      const integration = card.getByTestId('integration-status-badge');
      await expect(integration).toHaveAttribute('data-integration-status', 'pending');
      await expect(integration).toContainText('NICHT integriert');
      await expect(integration).not.toContainText('kein Branch');

      const context = card.getByTestId('task-card-change-context');
      await expect(context).toContainText(DELIVERY_REF);
      await expect(context).toContainText('commit discovery pending');
      await expect(context).not.toContainText('main checkout');
      await expect(context).not.toContainText('no code changes');

      mkdirSync(RESULTS, { recursive: true });
      const screenshotPath = join(RESULTS, `board-delivery-ref-${theme}.png`);
      await card.screenshot({ path: screenshotPath });
      await testInfo.attach(`board-delivery-ref-${theme}.png`, {
        path: screenshotPath,
        contentType: 'image/png',
      });
    });
  }
});
