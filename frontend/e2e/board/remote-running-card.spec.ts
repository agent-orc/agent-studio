import { test, expect, type Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';

const PROJECT = 'remote-operations';
const WATCH_PATH = 'C:/fixtures/remote-operations';
const RESULTS_DIR = process.env['JOB_RESULTS_DIR'] ?? '../results/AGT-2415';
mkdirSync(RESULTS_DIR, { recursive: true });
const ACQUIRED_AT = new Date(Date.now() - 2 * 60_000).toISOString();
const HEARTBEAT_AT = new Date().toISOString();

const REMOTE_TASK = {
  id: 'AGT-2134-remote-proof',
  key: 'AGT-2134',
  taskKey: `${WATCH_PATH}::AGT-2134-remote-proof`,
  title: 'Remote runner board liveness proof',
  state: '3-progress',
  order: 1,
  agent: 'codex',
  cliType: 'codex',
  createdAt: ACQUIRED_AT,
  watchPath: WATCH_PATH,
  projectName: PROJECT,
  folderPath: `${WATCH_PATH}/3-progress/AGT-2134-remote-proof`,
  lastActivity: HEARTBEAT_AT,
  sessionName: null,
  model: 'gpt-5.6-codex',
  execution: null,
  runner: {
    runnerId: 'agent-runner-01@linux-host',
    runnerName: 'agent-runner-01',
    hostname: 'linux-host',
    backendName: 'remote-runner',
    isRemote: true,
    leaseId: 'lease-remote-proof',
    fencingToken: 11,
    acquiredAt: ACQUIRED_AT,
  },
  executionLocation: {
    state: 'remote-running',
    executionKind: 'remote',
    runnerId: 'agent-runner-01@linux-host',
    clientId: 'agent-runner-01@linux-host',
    hostDisplayName: 'agent-runner-01',
    configuredRunnerId: 'agent-runner-01@linux-host',
    startedAt: ACQUIRED_AT,
    lastHeartbeat: HEARTBEAT_AT,
    lastActivityAt: HEARTBEAT_AT,
    processId: 4242,
    sessionId: null,
    branch: 'task/AGT-2134',
    worktreePath: '/worktrees/AGT-2134',
    connectionState: 'connected',
    leaseState: 'active',
    trustReason: 'The task server currently holds a fenced run lease with a recent heartbeat.',
  },
  runActivity: { kind: 'no-active-run', attempt: 0 },
  liveStatus: {
    attempt: 1,
    activeStep: null,
    nextSteps: [{ stepId: 'core-agent-run', displayName: 'Agent run' }],
    queue: null,
    latestEventAt: HEARTBEAT_AT,
  },
  commit: null,
  commits: [],
  ownerClientId: 'local-default',
  tags: [],
};

const REMOTE_TASKS = Array.from({ length: 8 }, (_, index) => ({
  ...REMOTE_TASK,
  id: `AGT-${2410 - index}-remote-proof`,
  key: `AGT-${2410 - index}`,
  taskKey: `${WATCH_PATH}::AGT-${2410 - index}-remote-proof`,
  title: index === 0 ? REMOTE_TASK.title : `Remote worker ${index + 1}`,
  order: index + 1,
  folderPath: `${WATCH_PATH}/3-progress/AGT-${2410 - index}-remote-proof`,
  runner: {
    ...REMOTE_TASK.runner,
    leaseId: `lease-remote-proof-${index + 1}`,
    fencingToken: 11 + index,
  },
}));

const GROUPED = {
  backlog: [], preparation: [], orchestratorPrep: [], ready: [],
  progress: REMOTE_TASKS, failedPickup: [], review: [], autoReview: [],
  humanReview: [], completed: [], archive: [],
};

async function installRoutes(page: Page): Promise<void> {
  await page.route('**/api/**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => undefined));
  await page.route('**/api/v1/studio/auth/status', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        profile: 'local',
        bootstrapRequired: false,
        authenticated: true,
        user: null,
      }),
    }));
  await page.route('**/api/v1/studio/board**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(GROUPED) }));
  await page.route('**/api/watch-paths**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH }]),
    }));
  await page.route('**/api/environment**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ isDev: false, devTools: {} }) }));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        projects: {
          [PROJECT]: {
            projectName: PROJECT,
            mode: 'auto-continuous',
            activeJobId: null,
            activeExecution: null,
            queuedJobIds: [],
          },
        },
      }),
    }));
  await page.route('**/api/git/summary**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route(/\/api\/git\/hygiene(\?|$)/, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '{}' }));
  await page.route('**/api/agent-rules**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route('**/api/clients', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route('**/api/cli/usage**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ sessions: [] }) }));
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ snapshots: [] }) }));
  await page.route('**/api/tags', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
}

async function openBoard(page: Page): Promise<void> {
  await page.addInitScript(() => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'board', projectName: '__all__' }],
      activeKey: 'board:__all__',
    }));
  });
  await installRoutes(page);
  await page.goto('/?includeFixtures=true');
  await expect(page.locator('[data-testid="studio-board"], [data-testid="kanban-dashboard"]').first())
    .toBeVisible();
}

test.describe('Remote lease drives the board running state', () => {
  for (const theme of ['dark', 'light'] as const) {
    test(`remote card is visibly running without local activeExecution (${theme})`, async ({ page }, testInfo) => {
      await openBoard(page);
      await page.evaluate((selectedTheme) => {
        document.documentElement.dataset['studioTheme'] = selectedTheme;
        localStorage.setItem('atp.studio.theme', selectedTheme);
      }, theme);
      await page.setViewportSize({ width: 1600, height: 1000 });

      const card = page.getByTestId('task-card').filter({ hasText: REMOTE_TASK.title });
      await expect(card).toBeVisible();
      await expect(card).toHaveAttribute('data-running', 'true');

      const runner = card.getByTestId('execution-location-badge');
      await expect(runner).toHaveAttribute('data-execution-state', 'remote-running');
      await expect(runner).toContainText('Host · agent-runner-01');
      const current = card.getByTestId('task-live-current');
      await expect(current).toContainText('Running remote on agent-runner-01');
      await expect(card.getByTestId('task-live-status')).toHaveAttribute('data-live-tone', 'active');
      await expect(card.getByTestId('task-live-status')).toContainText(/Active for 2m\d{2}s/);
      await expect(card.getByTestId('task-live-status')).not.toContainText('No active run');
      await expect(card.getByTestId('task-card-stalled')).toHaveCount(0);
      await expect(page.getByTestId('status-bar-running')).toContainText('remote 8');

      // Background feeds outside this fixture's board scope may surface a
      // generic error dialog. It is unrelated to the card projection and must
      // not cover the persisted visual proof.
      const errorClose = page.getByTestId('error-dialog-close');
      if (await errorClose.isVisible().catch(() => false)) await errorClose.click();
      await page.evaluate(() => {
        document.querySelectorAll('app-error-dialog, vite-error-overlay').forEach((node) => node.remove());
      });

      const screenshot = await page.screenshot({
        path: join(RESULTS_DIR, `remote-wave-after-${theme}--mocked.png`),
        fullPage: false,
      });
      await testInfo.attach(`remote-wave-after-${theme}--mocked.png`, { body: screenshot, contentType: 'image/png' });
    });
  }
});
