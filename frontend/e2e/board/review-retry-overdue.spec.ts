import { expect, test, type Page, type Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

/**
 * AGT-3848 (operator crisis playthrough AGT-3648, finding S6-F2): after the
 * review was lost the card said "Automatic retry 1 of 2 at 11:37 PM". The
 * retry never started, and the card kept that promise until the watchdog
 * parked it.
 *
 * The retry scheduler is disabled here: the board keeps reporting the same
 * `retry-scheduled` queue after the due time, which is what the backend
 * reports when no scheduler tick fires the retry. Only the browser clock
 * moves past the due time.
 */

const PROJECT = 'Retry overdue';
const WATCH_PATH = '/fixtures/retry-overdue';
const RESULTS = process.env.JOB_RESULTS_DIR ?? join(process.cwd(), '..', 'results');
const LOST_AT = '2026-11-15T23:36:10Z';
const DUE_AT = '2026-11-15T23:37:10Z';
// The backend's ParkAt: due + 5 minute overdue limit + one 30 second watchdog tick, rounded up.
const PARK_AT = '2026-11-15T23:43:00Z';

const CARD = {
  id: 'agt-retry-overdue',
  taskKey: `${WATCH_PATH}::agt-retry-overdue`,
  key: 'AGT-RETRY-OVERDUE',
  title: 'Review lost with a retry nobody fires',
  state: '4-auto-review',
  order: 1,
  agent: 'codex',
  cliType: 'codex',
  createdAt: '2026-11-15T23:00:00Z',
  watchPath: WATCH_PATH,
  projectName: PROJECT,
  folderPath: `${WATCH_PATH}/4-auto-review/agt-retry-overdue`,
  lastActivity: LOST_AT,
  liveStatus: {
    attempt: 1,
    activeStep: null,
    nextSteps: [],
    queue: {
      kind: 'review',
      state: 'retry-scheduled',
      outcome: 'infrastructureFailure',
      lost: { cause: 'host-restarted', classification: 'ExecutorRestarted', hostId: 'lab-review-host',
        executorId: 'first-run-review', at: LOST_AT },
      retry: { number: 1, budget: 2, dueAt: DUE_AT, parkAt: PARK_AT },
    },
    latestEventAt: LOST_AT,
  },
};

function json(route: Route, body: unknown): Promise<void> {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

async function installRoutes(page: Page): Promise<void> {
  await page.route('**/api/**', route => {
    const url = route.request().url();
    if (url.includes('/api/v1/studio/auth/status')) {
      return json(route, { profile: 'local', bootstrapRequired: false, authenticated: true });
    }
    if (url.includes('/batch-gate/state')) return json(route, {
      plan: { decision: 'wait', reason: 'no batch', proposedMembers: [], targetSize: 2, upcoming: [] },
      pending: [], batches: [], members: {},
    });
    if (url.includes('/api/tasks/archive')) return json(route, { items: [], total: 0 });
    if (url.includes('/api/v1/studio/board')) {
      return json(route, {
        backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [], failedPickup: [],
        codeNotComplete: [], review: [], autoReview: [CARD], humanReview: [], escalated: [],
        completed: [], archive: [],
      });
    }
    if (/\/api\/(?:tasks|jobs)(\?|$)/.test(url)) return json(route, [CARD]);
    if (url.includes('/api/watch-paths')) return json(route, [{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH }]);
    if (url.includes('/api/clients')) return json(route, [{ id: 'local-default', displayName: 'Local', kind: 'agent-instance' }]);
    if (url.includes('/api/v1/studio/runner/status')) return json(route, { projects: {} });
    if (url.includes('/api/environment')) return json(route, { isDev: false, devTools: {} });
    if (url.includes('/api/cli/')) return json(route, { snapshots: [], sessions: [] });
    return json(route, []);
  });
}

function clock(iso: string): string {
  return new Date(iso).toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit' });
}

test.describe('review retry overdue', () => {
  test.use({ viewport: { width: 1280, height: 900 } });

  test('a retry that never starts reads overdue and names when the watchdog parks the card', async ({ page }) => {
    test.setTimeout(120_000);
    mkdirSync(RESULTS, { recursive: true });
    await page.clock.install({ time: new Date('2026-11-15T23:36:40Z') });
    await page.addInitScript(() => {
      if (localStorage.getItem('atp.studio.tabs.v1')) return;
      localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
        v: 1, tabs: [{ kind: 'board', projectName: '__all__' }], activeKey: 'board:__all__',
      }));
    });
    await installRoutes(page);
    await page.goto('/?includeFixtures=true');
    await page.addStyleTag({ content: '.dialog__overlay, [data-testid="offline-banner"] { display: none !important; }' });
    await dismissDevErrorDialog(page);
    await page.getByRole('tab', { name: 'All projects · Board' }).click();
    await expect(page.getByTestId('studio-board')).toBeVisible();

    const card = page.getByTestId('task-card').filter({ hasText: CARD.title });
    const status = card.getByTestId('task-live-status');
    const loss = card.getByTestId('task-live-review-loss');

    // 1. Before the due time the retry is a promise.
    await expect(loss).toContainText(`Automatic retry 1 of 2 at ${clock(DUE_AT)}.`);
    await expect(status).toHaveAttribute('data-live-tone', 'waiting');

    // 2. 67 seconds past the due time, as in the playthrough, the backend still says retry-scheduled.
    await page.clock.fastForward(97_000);
    await expect(loss).toContainText(
      `Automatic retry 1 of 2 overdue since ${clock(DUE_AT)} · parks in Escalated by ${clock(PARK_AT)}.`);
    await expect(loss).not.toContainText(`at ${clock(DUE_AT)}.`);
    await expect(status).toHaveAttribute('data-live-tone', 'stalled');
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await card.screenshot({ path: join(RESULTS, `card-retry-overdue-1280-${theme}.png`) });
    }

    // 3. Past the park time nothing is promised any more.
    await page.clock.fastForward(6 * 60_000);
    await expect(loss).toContainText(`not parked by ${clock(PARK_AT)} · operator decision needed`);
  });
});
