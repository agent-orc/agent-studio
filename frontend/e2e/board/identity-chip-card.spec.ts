import { expect, test, type Page, type Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import * as path from 'node:path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

/**
 * AGT-3709 (playthrough AGT-3649 finding F16), board half.
 *
 * - A Ready card a fresh host refuses with `host-capability-missing` for
 *   `model-acceptance` says so on the card: which model the host has not
 *   accepted, and for how long, instead of "Queued, but not pickable".
 * - A slice card's Dossier chip no longer overlaps its title at 390 px: the
 *   chip takes its own line and truncates.
 *
 * Fully mocked (`--mocked` screenshots); the hold projection itself is covered
 * by `PickupHoldPolicyTests`.
 */

const PROJECT = 'Agent Studio Platform Operations';
const WATCH_PATH = '/fixtures/card-polish';
const RESULTS_DIR = process.env['JOB_RESULTS_DIR']?.trim()
  ? path.resolve(process.env['JOB_RESULTS_DIR'])
  : path.resolve('test-results', 'identity-chip-card');
const DESKTOP = { width: 1440, height: 900 };
const PHONE = { width: 390, height: 844 };

function json(route: Route, body: unknown, status = 200): Promise<void> {
  return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
}

function card(id: string, over: Record<string, unknown>) {
  return {
    id,
    taskKey: `${WATCH_PATH}::${id}`,
    title: id,
    state: '2-ready',
    order: 1,
    agent: 'claude',
    cliType: 'claude',
    model: 'claude-opus-5',
    thinkingLevel: 'high',
    createdAt: '2026-11-09T09:00:00.000Z',
    lastActivity: '2026-11-09T09:30:00.000Z',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/2-ready/${id}`,
    sessionName: null,
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    commits: [],
    ownerClientId: 'local-default',
    tags: [],
    ...over,
  };
}

const SA_ID = 'service-account:sa_62c9a37021db4c8e91f0d3a7b5c2e648';
const HUMAN_ID = 'usr_3f9a1c07d2e84b56a0c1e9f7';
const RUNNER_ID = 'runner_d9a38223247c41e4bab44778397c0794';
const NOW = new Date().toISOString();

function runningCard(id: string, owner: string, key: string) {
  return card(id, {
    key, title: 'Show host names instead of runner ids', state: '3-progress', order: 1,
    ownerClientId: owner,
    folderPath: `${WATCH_PATH}/3-progress/${id}`,
    runner: { runnerId: RUNNER_ID, runnerName: null, hostname: 'h', backendName: 'remote-runner', isRemote: true,
      leaseId: 'l', fencingToken: 1, acquiredAt: NOW },
    executionLocation: {
      state: 'remote-running', executionKind: 'remote', runnerId: RUNNER_ID, clientId: RUNNER_ID,
      hostDisplayName: 'agent-runner-01', configuredRunnerId: RUNNER_ID, startedAt: NOW, lastHeartbeat: NOW,
      lastActivityAt: NOW, processId: 1, sessionId: null, branch: 'task/x', worktreePath: '/w',
      connectionState: 'connected', leaseState: 'active', trustReason: 'lease',
    },
    runActivity: { kind: 'no-active-run', attempt: 0 },
    liveStatus: { attempt: 1, activeStep: null, nextSteps: [], queue: null, latestEventAt: NOW },
  });
}

function grouped() {
  return {
    backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [runningCard('sa', SA_ID, 'AGT-4034'), runningCard('human', HUMAN_ID, 'AGT-4035')],
    failedPickup: [], codeNotComplete: [], review: [], autoReview: [], humanReview: [], escalated: [],
    completed: [], archive: [],
  };
}

async function installRoutes(page: Page): Promise<void> {
  await page.route('**/update/status', route => json(route, { phase: 'idle', isRunning: false, behindBy: 0 }));
  await page.route('**/hubs/v1/studio/negotiate**', route => json(route, {
    connectionId: 'card-polish-e2e',
    connectionToken: 'card-polish-e2e',
    negotiateVersion: 1,
    availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text', 'Binary'] }],
  }));
  await page.routeWebSocket('**/hubs/v1/studio**', socket => {
    socket.onMessage(message => {
      if (message.toString().includes('"protocol":"json"')) socket.send('{}\u001e');
    });
  });
  await page.route('**/api/**', route => json(route, []));
  await page.route('**/api/v1/studio/auth/status', route => json(route, {
    profile: 'local', bootstrapRequired: false, authenticated: false, user: null,
  }));
  await page.route('**/api/crash-recovery/pending', route => json(route, { pending: [] }));
  await page.route('**/api/v1/studio/board**', route => json(route, grouped()));
  await page.route('**/api/watch-paths**', route => json(route, [
    { name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH },
  ]));
  await page.route('**/api/environment**', route => json(route, { isDev: false, devTools: {} }));
  await page.route('**/api/projects/settings**', route => json(route, {}));
  await page.route(/\/api\/v1\/workspaces(\?|$)/, route => json(route, [{
    id: 'ws-default', displayName: 'Default', sortOrder: 0, isDefault: true, color: null,
    createdAt: '2026-11-01T00:00:00Z',
    projects: [{
      sourceType: 'local', id: 'PROJ-0098', displayName: PROJECT, shortCode: 'AGT', workspaceId: 'ws-default',
      color: null, cliDefault: null, modelDefault: null, sortOrder: 0, storageLocation: WATCH_PATH,
      repositoryPath: WATCH_PATH, rootPath: WATCH_PATH, repositoryUrl: null, urls: [], archived: false,
    }],
  }]));
  await page.route(/\/api\/tasks\/archive(\?|$)/, route => json(route, { items: [], total: 0, offset: 0, limit: 50 }));
  await page.route(/\/api\/clients\/[^/]+\/telemetry/, route => json(route, { points: [], findings: [], window: '14d' }));
  await page.route('**/api/clients', route => json(route, [
    { id: 'local-default', displayName: 'Local', kind: 'agent-instance' },
  ]));
  await page.route('**/api/cli/quota**', route => json(route, {
    at: '2026-11-09T09:30:00.000Z', ttlSeconds: 600, snapshots: [],
  }));
  await page.route('**/api/runner/orchestrator-feed**', route => json(route, {
    entries: [], generatedAtUtc: '2026-11-09T09:30:00.000Z',
  }));
  // Unknown host fleet: an empty list would read as "no host reachable" and add
  // a provider wait to every card, which is not the state under test.
  await page.route('**/api/v1/management/remote-hosts', route => json(route, { error: 'not mocked' }, 503));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, route => json(route, {
    projects: { [PROJECT]: { projectName: PROJECT, activeJobId: null, activeExecution: null } },
  }));
}

/** Opens the all-projects board tab, as the other mocked board specs do. */
async function openBoard(page: Page): Promise<void> {
  await page.setViewportSize(DESKTOP);
  await page.addInitScript(() => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'board', projectName: '__all__' }],
      activeKey: 'board:__all__',
    }));
  });
  await installRoutes(page);
  await page.goto('/#/board');
  await page.addStyleTag({
    content: 'app-error-dialog, app-offline-banner, [data-testid="offline-banner"], [data-testid="error-dialog-overlay"] { display: none !important; }',
  });
  await dismissDevErrorDialog(page);
  await page.getByRole('tab', { name: 'All projects · Board' }).click();
  await expect(page.getByTestId('lane-2-ready')).toBeVisible({ timeout: 15_000 });
}

/** At 390 px the shell hides the Explorer and the activity rail (AGT-3836), so the board gets the full width. */
async function toPhone(page: Page): Promise<void> {
  await page.setViewportSize(PHONE);
  await page.waitForTimeout(300);
}

async function shot(page: Page, name: string, target?: import('@playwright/test').Locator): Promise<void> {
  const file = path.join(RESULTS_DIR, `${name}--mocked.png`);
  if (target) await target.screenshot({ path: file });
  else await page.screenshot({ path: file, fullPage: false });
}

test.describe('Identity chip and host alias on the board card (AGT-4034)', () => {
  test.beforeAll(() => mkdirSync(RESULTS_DIR, { recursive: true }));
  test.afterEach(async ({ page }) => { await page.unrouteAll({ behavior: 'ignoreErrors' }); });

  for (const kind of ['sa', 'human'] as const) {
    test(`key stays whole, no raw ids (${kind} creator)`, async ({ page }) => {
      await openBoard(page);
      const card = page.getByTestId('task-card').filter({ has: page.locator(`[data-testid="task-card-key"]`) })
        .nth(kind === 'sa' ? 0 : 1);
      await expect(card).toBeVisible();
      const label = process.env['SHOT_LABEL'] ?? 'after';
      for (const theme of ['light', 'dark'] as const) {
        await setTheme(page, theme);
        await page.mouse.move(DESKTOP.width - 10, DESKTOP.height / 2);
        await shot(page, `${label}-${kind}-normal-${theme}`, card);
      }
      await toPhone(page);
      await page.getByLabel('Board lane').selectOption({ label: 'In progress' }).catch(
        () => page.getByLabel('Board lane').selectOption('3-progress'));
      await expect(card).toBeVisible();
      await card.scrollIntoViewIfNeeded();
      for (const theme of ['light', 'dark'] as const) {
        await setTheme(page, theme);
        await shot(page, `${label}-${kind}-narrow-${theme}`, card);
      }
      const text = await page.getByTestId('lane-3-progress').innerText();
      if (label === 'after') {
        expect(text).not.toMatch(/runner_[0-9a-f]{8,}/);
        expect(text).not.toMatch(/sa_[0-9a-f]{8,}/);
        const keyBox = await card.getByTestId('task-card-key').boundingBox();
        const cardBox = await card.boundingBox();
        expect(keyBox!.x + keyBox!.width).toBeLessThanOrEqual(cardBox!.x + cardBox!.width);
        const keyText = await card.getByTestId('task-card-key').evaluate(
          (el: HTMLElement) => el.scrollWidth <= el.clientWidth + 1);
        expect(keyText).toBe(true);
      }
    });
  }
});
