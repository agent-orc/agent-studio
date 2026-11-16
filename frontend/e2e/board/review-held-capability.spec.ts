import { expect, test, type Page, type Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

/**
 * AGT-3840 (playthrough AGT-3649, finding n4): a review the executor could
 * not claim because provider-auth:codex was missing read "Review host
 * saturated: load/core 2.0 > 1.0" for 20 minutes, and Execution Hosts showed
 * the review host Online without the held capability.
 *
 * The board reports what the backend projects for that state: the refused
 * claim's key and host, ahead of host load. The host link opens the review
 * role on Execution Hosts focused on the held key, with the probe detail and
 * a re-probe action that asks the role to re-probe exactly that key.
 */

const PROJECT = 'Held review';
const WATCH_PATH = '/fixtures/held-review';
const RESULTS = process.env.JOB_RESULTS_DIR ?? join(process.cwd(), '..', 'results');
const RUNNER = 'kori-review-01-runner';
const HOST = 'kori-review-01';
const KEY = 'provider-auth:codex';
const DETAIL = 'codex login status timed out after 30s';

function card(now: string) {
  return {
    id: 'agt-held-review',
    taskKey: `${WATCH_PATH}::agt-held-review`,
    key: 'AGT-HELD-REVIEW',
    title: 'Review held on a missing provider login',
    state: '4-auto-review',
    order: 1,
    agent: 'codex',
    cliType: 'codex',
    createdAt: now,
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/4-auto-review/agt-held-review`,
    lastActivity: now,
    liveStatus: {
      attempt: 1,
      activeStep: null,
      nextSteps: [],
      queue: {
        kind: 'review',
        state: 'queued',
        reason: `Review cannot be claimed: ${RUNNER} on host ${HOST} lacks ${KEY} (probe indeterminate: ${DETAIL}).`,
        unclaimable: {
          capabilities: [KEY], executorId: RUNNER, hostId: HOST,
          cause: 'probe-indeterminate', detail: DETAIL, observedAt: now,
        },
      },
      latestEventAt: now,
    },
  };
}

function capability(key: string, category: string, now: string, extra: Record<string, unknown> = {}) {
  return {
    key, category, advertisedStatus: 'ready', healthState: 'healthy', reason: null,
    advertisedAt: now, freshUntil: new Date(Date.parse(now) + 180_000).toISOString(), isFresh: true,
    consecutiveFailures: 0, affectedClaims: [], recoveryHistory: [], ...extra,
  };
}

function snapshot(now: string) {
  return {
    runnerId: RUNNER, name: HOST, hostId: HOST, instanceId: `${HOST}:1`, runnerVersion: '1.0.0',
    protocolVersion: 1, status: 'active', registeredAt: now, lastSeenAt: now,
    hostAdmission: { hostId: HOST, admissionState: 'open' },
    // The playthrough state: every advertisement reads ready and healthy, so the role is Online,
    // but the codex probe is indeterminate and claims withhold the key.
    capabilities: [
      capability('executor:review', 'executor', now),
      capability(KEY, 'provider-auth', now, { signal: 'indeterminate', detail: DETAIL }),
    ],
    telemetry: null,
    heldCapabilities: [{
      key: KEY, state: 'indeterminate', cause: 'probe-indeterminate', detail: DETAIL,
      observedAt: now, waitingTaskKeys: ['AGT-HELD-REVIEW'],
    }],
  };
}

function json(route: Route, body: unknown, status = 200): Promise<void> {
  return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
}

async function installRoutes(page: Page, reprobes: unknown[]): Promise<void> {
  const now = new Date().toISOString();
  const held = card(now);
  await page.route('**/api/**', route => {
    const request = route.request();
    const url = request.url();
    if (url.includes(`/api/v1/management/remote-hosts/${RUNNER}/capabilities/reprobe`) && request.method() === 'POST') {
      reprobes.push(request.postDataJSON());
      return json(route, { runnerId: RUNNER, capability: KEY, requested: true }, 202);
    }
    if (/\/api\/v1\/management\/remote-hosts(\?|$)/.test(url)) return json(route, [snapshot(now)]);
    if (url.includes('/batch-gate/state')) return json(route, {
      plan: { decision: 'wait', reason: 'no batch', proposedMembers: [], targetSize: 2, upcoming: [] },
      pending: [], batches: [], members: {},
    });
    if (url.includes('/api/tasks/archive')) return json(route, { items: [], total: 0 });
    if (url.includes('/api/v1/studio/board')) {
      return json(route, {
        backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [], failedPickup: [],
        codeNotComplete: [], review: [], autoReview: [held], humanReview: [], escalated: [],
        completed: [], archive: [],
      });
    }
    if (/\/api\/(?:tasks|jobs)(\?|$)/.test(url)) return json(route, [held]);
    if (url.includes('/api/watch-paths')) return json(route, [{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH }]);
    if (/\/api\/clients(\?|$)/.test(url)) return json(route, [
      { id: 'local-default', displayName: 'Local', kind: 'agent-instance', registeredAt: now, lastSeenAt: now },
      { id: RUNNER, displayName: HOST, kind: 'service', registeredAt: now, lastSeenAt: now,
        runnerDaemonState: 'running', runnerActiveSlots: 0, runnerAvailableSlots: 2 },
    ]);
    // Everything else is the isolated e2e backend's own answer.
    return route.fallback();
  });
}

test.describe('review held on a missing capability', () => {
  test.use({ viewport: { width: 1280, height: 900 } });

  test('the card names the missing key and host, and the host link opens the held capability', async ({ page }) => {
    test.setTimeout(120_000);
    mkdirSync(RESULTS, { recursive: true });
    const reprobes: unknown[] = [];
    await page.addInitScript(() => {
      if (localStorage.getItem('atp.studio.tabs.v1')) return;
      localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
        v: 1, tabs: [{ kind: 'board', projectName: '__all__' }], activeKey: 'board:__all__',
      }));
    });
    await installRoutes(page, reprobes);
    await page.goto('/?includeFixtures=true');
    await page.addStyleTag({ content: '.dialog__overlay, [data-testid="offline-banner"] { display: none !important; }' });
    await dismissDevErrorDialog(page);
    await page.getByRole('tab', { name: 'All projects · Board' }).click();
    await expect(page.getByTestId('studio-board')).toBeVisible();

    // 1. The card names the key and its host, not the host load.
    const heldCard = page.getByTestId('task-card').filter({ hasText: 'Review held on a missing provider login' });
    const status = heldCard.getByTestId('task-live-status');
    const line = heldCard.getByTestId('task-live-review-unclaimable');
    await expect(line).toHaveText(/^\s*Review held: provider-auth:codex missing on kori-review-01 · probe indeterminate\s*$/);
    await expect(status).toHaveAttribute('data-live-tone', 'stalled');
    await expect(status).toContainText(DETAIL);
    await expect(status).not.toContainText('saturated');
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await heldCard.screenshot({ path: join(RESULTS, `card-review-held-capability-1280-${theme}.png`) });
    }
    await setTheme(page, 'light');

    // 2. The host link opens the review role on Execution Hosts, focused on the held key.
    // Wide enough that the settings pane keeps the host list beside the detail.
    await page.setViewportSize({ width: 1920, height: 1080 });
    await heldCard.getByTestId('task-live-unclaimable-host-link').click();
    await expect(page).toHaveURL(/#\/workspace\/settings\/environment\?host=kori-review-01-runner&capability=provider-auth%3Acodex/);
    const role = page.locator(`[data-host-row="${RUNNER}"]`);
    await expect(role).toHaveAttribute('aria-current', 'true', { timeout: 30_000 });
    // The role stays Online, and its row now names the held key.
    await expect(role).toHaveAttribute('data-status', 'online');
    await expect(role.getByTestId('remote-host-role-held')).toHaveText('Held: provider-auth:codex');

    const heldRow = page.getByTestId(`remote-host-held-${KEY}`);
    await expect(heldRow).toBeVisible();
    await expect(heldRow).toHaveAttribute('data-focused', 'true');
    await expect(heldRow).toHaveAttribute('data-state', 'indeterminate');
    await expect(page.getByTestId(`remote-host-held-state-${KEY}`)).toContainText('probe indeterminate');
    await expect(page.getByTestId(`remote-host-held-detail-${KEY}`)).toHaveText(DETAIL);
    await expect(heldRow).toContainText('AGT-HELD-REVIEW');
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.screenshot({ path: join(RESULTS, `execution-hosts-held-capability-1920-${theme}.png`) });
    }
    await setTheme(page, 'light');

    // 3. Re-probe asks this role to re-probe exactly the held key.
    await page.getByTestId(`remote-host-held-reprobe-${KEY}`).click();
    await expect.poll(() => reprobes).toEqual([{ capability: KEY }]);
  });
});
