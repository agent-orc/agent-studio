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

const PROJECT = 'Card polish fixture';
const WATCH_PATH = '/fixtures/card-polish';
const RESULTS_DIR = process.env['JOB_RESULTS_DIR']?.trim()
  ? path.resolve(process.env['JOB_RESULTS_DIR'])
  : path.resolve('test-results', 'card-host-capability-and-dossier-chip');
const DESKTOP = { width: 1440, height: 900 };
const PHONE = { width: 390, height: 844 };
const HOST_ID = 'host-capability';
const SLICE_ID = 'dossier-slice';
const REFUSED_AT = new Date(Date.now() - 9.5 * 60_000).toISOString();
const REFUSAL = 'Host capability missing on agent-runner-02: model-acceptance:claude:claude-opus-5. Upgrade or repair the host probe.';

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

function hostCapabilityCard() {
  return card(HOST_ID, {
    key: 'AGT-3701',
    title: 'Concept: billing export',
    order: 1,
    liveStatus: { attempt: 1, activeStep: null, nextSteps: [], latestEventAt: REFUSED_AT },
    pickupHold: {
      mechanism: 'host-capability-missing',
      reason: `Runner agent-runner-02 refused this card (host-capability-missing): ${REFUSAL}`,
      sinceUtc: REFUSED_AT,
      unsatisfiable: false,
      resolutions: [],
    },
    executionLocation: {
      state: 'queued-remote',
      executionKind: 'remote',
      lastHeartbeat: new Date().toISOString(),
      lastRejection: {
        code: 'host-capability-missing',
        runnerId: 'runner-2',
        runnerName: 'agent-runner-02',
        reason: REFUSAL,
        rejectedAtUtc: REFUSED_AT,
      },
    },
  });
}

function sliceCard() {
  return card(SLICE_ID, {
    key: 'AGT-3702',
    title: 'Slice 2: move the export scheduler behind the billing feature flag',
    order: 2,
    dossier: {
      path: 'docs/operations/billing-export-concept/index.html',
      section: '§4.2',
      decision: 'BE-D3',
      implementationIndex: 2,
    },
  });
}

function grouped() {
  return {
    backlog: [], preparation: [], orchestratorPrep: [], ready: [hostCapabilityCard(), sliceCard()], progress: [],
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

/** Closes the Explorer rail so the 390 px board shows a full card column. */
async function toPhone(page: Page): Promise<void> {
  await page.setViewportSize(PHONE);
  const explorer = page.getByTestId('studio-ab-explorer');
  if (await explorer.evaluate(el => el.classList.contains('studio-ab__btn--active')).catch(() => false)) {
    await explorer.click();
  }
  await page.waitForTimeout(300);
}

async function shot(page: Page, name: string, target?: import('@playwright/test').Locator): Promise<void> {
  const file = path.join(RESULTS_DIR, `${name}--mocked.png`);
  if (target) await target.screenshot({ path: file });
  else await page.screenshot({ path: file, fullPage: false });
}

function overlaps(a: { x: number; y: number; width: number; height: number },
                  b: { x: number; y: number; width: number; height: number }): boolean {
  return a.x < b.x + b.width && b.x < a.x + a.width && a.y < b.y + b.height && b.y < a.y + a.height;
}

test.describe('Card polish (AGT-3709)', () => {
  test.beforeAll(() => mkdirSync(RESULTS_DIR, { recursive: true }));
  test.afterEach(async ({ page }) => {
    await page.unrouteAll({ behavior: 'ignoreErrors' });
  });

  test('a Ready card held by a host capability names the model and the wait', async ({ page }) => {
    await openBoard(page);

    const held = page.getByTestId('task-card').filter({ hasText: 'Concept: billing export' });
    await expect(held).toBeVisible();
    await expect(held).toContainText('host lacks the model');
    await expect(held.getByTestId('pickup-hold-headline')).toHaveText('Held: the host has not accepted claude-opus-5');
    await expect(held.getByTestId('pickup-hold-mechanism')).toHaveText('Host capability');
    await expect(held.getByTestId('pickup-hold-age')).toContainText(/held for 9m/);
    // The refusal sentence renders once, in the durable rejection record.
    await expect(held.getByTestId('pickup-hold-reason')).toHaveCount(0);
    await expect(held.getByTestId('remote-dispatch-rejection')).toContainText('model-acceptance:claude:claude-opus-5');

    await page.mouse.move(DESKTOP.width - 10, DESKTOP.height / 2);
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await shot(page, `host-capability-card-desktop-${theme}`, held);
    }
    await toPhone(page);
    await held.scrollIntoViewIfNeeded();
    await shot(page, 'host-capability-card-390-dark', held);
  });

  test('the Dossier chip does not overlap the slice title at 390 px', async ({ page }) => {
    await openBoard(page);

    const slice = page.getByTestId('task-card').filter({ has: page.getByTestId('task-card-dossier') });
    await expect(slice).toBeVisible();
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.mouse.move(DESKTOP.width - 10, DESKTOP.height / 2);
      await shot(page, `dossier-slice-card-desktop-${theme}`, slice);
    }

    for (const theme of ['dark', 'light'] as const) {
      await setTheme(page, theme);
      await toPhone(page);
      await slice.scrollIntoViewIfNeeded();
      const chip = await slice.getByTestId('task-card-dossier').boundingBox();
      const title = await slice.locator('[data-testid="task-card-title"] [data-truncatable]').boundingBox();
      const cardBox = await slice.boundingBox();
      expect(chip && title && cardBox).toBeTruthy();
      expect(overlaps(chip!, title!), `chip ${JSON.stringify(chip)} overlaps title ${JSON.stringify(title)}`).toBe(false);
      expect(chip!.x + chip!.width).toBeLessThanOrEqual(cardBox!.x + cardBox!.width + 0.5);
      await shot(page, `dossier-slice-card-390-${theme}`, slice);
      await shot(page, `dossier-slice-board-390-${theme}`);
      await page.setViewportSize(DESKTOP);
    }
  });
});
