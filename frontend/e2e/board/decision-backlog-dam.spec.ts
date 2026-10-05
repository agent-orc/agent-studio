import { expect, test, type Page, type Route } from '@playwright/test';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

const PROJECT = 'fixture-decision-backlog';
const WATCH_PATH = 'C:/fixtures/decision-backlog';
const WAITING_KEYS = Array.from({ length: 10 }, (_, index) => `AGT-${2300 + index}`);

function task(id: string, key: string, title: string, state: string, order: number, dependsOn: string[] = []) {
  return {
    id,
    key,
    displayKey: key,
    taskKey: `${WATCH_PATH}::${id}`,
    title,
    state,
    order,
    agent: 'codex',
    cliType: 'codex',
    createdAt: '2026-07-24T08:00:00Z',
    lastActivity: '2026-07-24T09:00:00Z',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/${state}/${id}`,
    ownerClientId: 'local-default',
    commits: [],
    tags: [],
    references: { dependsOn, relatedTo: [], blockedBy: [], supersedes: [] },
  };
}

const PRIMARY_DECISION = {
  ...task('decision-primary', 'AGT-2182', 'Release the shared runner gate', '5-human-review', 1),
  orchestratorVerdict: 'accept',
  transitiveWaiters: { count: 10, keys: WAITING_KEYS },
};
const SECONDARY_DECISION = {
  ...task('decision-secondary', 'AGT-2190', 'Confirm migration fallback', '5-human-review', 2),
  transitiveWaiters: { count: 1, keys: WAITING_KEYS.slice(0, 1) },
};
const READY = WAITING_KEYS.map((key, index) =>
  task(`waiting-${index}`, key, `Waiting implementation ${index + 1}`, '2-ready', index + 1, ['AGT-2182']));
const ALL = [PRIMARY_DECISION, SECONDARY_DECISION, ...READY];

const GROUPED = {
  backlog: [],
  preparation: [],
  orchestratorPrep: [],
  ready: READY,
  progress: [],
  failedPickup: [],
  codeNotComplete: [],
  autoReview: [],
  review: [],
  humanReview: [PRIMARY_DECISION, SECONDARY_DECISION],
  escalated: [],
  completed: [],
  archive: [],
};

async function json(route: Route, body: unknown): Promise<void> {
  await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

async function installRoutes(page: Page): Promise<void> {
  await page.route('**/api/**', (route) => json(route, []));
  await page.route('**/api/tasks/archive**', (route) => json(route, {
    items: [],
    total: 0,
    offset: 0,
    limit: 50,
  }));
  await page.route('**/api/v1/studio/auth/status', (route) => json(route, {
    profile: 'local',
    bootstrapRequired: false,
    authenticated: true,
    user: null,
  }));
  await page.route(/\/api\/tasks(\?|$)/, (route) => json(route, ALL));
  await page.route('**/api/v1/studio/board**', (route) => json(route, GROUPED));
  await page.route('**/api/watch-paths**', (route) => json(route, [
    { name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH },
  ]));
  await page.route('**/api/environment**', (route) => json(route, {
    isDev: false,
    devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false },
  }));
  await page.route('**/api/cli/usage**', (route) => json(route, {
    at: '2026-08-08T08:00:00Z',
    sessions: [],
  }));
  await page.route('**/api/cli/quota**', (route) => json(route, {
    at: '2026-08-08T08:00:00Z',
    ttlSeconds: 600,
    snapshots: [],
  }));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, (route) => json(route, {
    projects: {
      [PROJECT]: {
        projectName: PROJECT,
        mode: 'manual',
        activeJobId: null,
        activeExecution: null,
        queuedJobIds: [],
      },
    },
  }));
}

async function openBoard(page: Page): Promise<void> {
  await page.setViewportSize({ width: 1600, height: 1000 });
  await page.addInitScript(() => {
    localStorage.setItem('atp.flag.vsCodeLayout', '1');
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'board', projectName: '__all__' }],
      activeKey: 'board:__all__',
    }));
    const hideFixtureNoise = () => document.querySelectorAll('app-error-dialog, app-offline-banner')
      .forEach((element) => ((element as HTMLElement).style.display = 'none'));
    addEventListener('DOMContentLoaded', () => {
      hideFixtureNoise();
      new MutationObserver(hideFixtureNoise).observe(document.body, { childList: true, subtree: true });
    }, { once: true });
  });
  await installRoutes(page);
  await page.goto('/?includeFixtures=true', { waitUntil: 'domcontentloaded' });
  await expect(page.getByTestId('studio-board')).toBeVisible({ timeout: 15_000 });
  await expect(page.getByTestId('decision-backlog')).toBeVisible();
  await dismissDevErrorDialog(page);
}

test.describe('transitive decision backlog dam', () => {
  test('explains each decision impact and links every waiting card', async ({ page }) => {
    await openBoard(page);

    const rows = page.getByTestId('decision-backlog').locator('[aria-expanded]');
    await expect(rows).toHaveCount(2);
    await expect(rows.nth(0)).toContainText('Your decision on AGT-2182 blocks 10 waiting cards');
    await expect(rows.nth(1)).toContainText('Your decision on AGT-2190 blocks 1 waiting card');

    const card = page.getByTestId('task-card').filter({ hasText: PRIMARY_DECISION.title });
    const badge = card.getByTestId('task-card-decision-dam');
    await expect(badge).toContainText('Dams 10 cards');
    await badge.hover();
    await expect(page.getByTestId('cac-tooltip')).toContainText(WAITING_KEYS.join(', '));

    await rows.nth(0).hover();
    const waiters = page.getByTestId('decision-backlog-waiters-AGT-2182');
    await expect(waiters.getByRole('button')).toHaveCount(10);
    const firstWaiter = waiters.getByTestId('decision-backlog-waiter-AGT-2300');
    await expect(firstWaiter).toContainText('AGT-2300');
    await expect(firstWaiter).toContainText('Waiting implementation 1');
    await expect(firstWaiter).toBeEnabled();
  });

  for (const theme of ['light', 'dark'] as const) {
    test(`captures the decision backlog in ${theme} theme`, async ({ page }) => {
      await openBoard(page);
      await setTheme(page, theme);
      await expect(page.locator('html')).toHaveAttribute('data-studio-theme', theme);
      const primaryEntry = page.getByTestId('decision-backlog-item-AGT-2182');
      await expect(primaryEntry).toContainText('10 waiting cards');
      await primaryEntry.click();
      await expect(page.getByTestId('decision-backlog-waiters-AGT-2182')).toBeVisible();
      await expect(
        page.getByTestId('task-card').filter({ hasText: PRIMARY_DECISION.title })
          .getByTestId('task-card-decision-dam'),
      ).toContainText('Dams 10 cards');

      const resultsDir = process.env['JOB_RESULTS_DIR'] ?? process.env['DECISION_BACKLOG_RESULTS_DIR'];
      if (resultsDir) {
        await page.screenshot({
          path: `${resultsDir}/decision-backlog-${theme}--mocked.png`,
          fullPage: false,
        });
      }
    });
  }
});
