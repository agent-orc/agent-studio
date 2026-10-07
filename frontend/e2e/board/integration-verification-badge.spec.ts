import * as path from 'node:path';
import { test, expect, type Page, type Route } from '@playwright/test';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

// AGT-3002: a delivery the integration branch contains is not integrated
// until a gate passed on that tree. The board badge tells an
// integrated-verified card from an integrated-unverified one, so a card that
// reached develop through an un-gated push never reads as a green merge.

const PROJECT = 'Integration truth';
const WATCH_PATH = '/fixtures/integration-truth';
const RESULTS_DIR = process.env['JOB_RESULTS_DIR']?.trim() || path.join(process.cwd(), 'test-results');
const TREE = 'b'.repeat(40);

function card(id: string, key: string, title: string, state: string, verification: unknown) {
  return {
    id,
    taskKey: `${WATCH_PATH}::${id}`,
    key,
    title,
    state,
    order: 1,
    agent: 'codex',
    cliType: 'codex',
    createdAt: '2026-09-28T18:00:00Z',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/${state}/${id}`,
    lastActivity: '2026-09-29T06:00:00Z',
    sessionName: null,
    model: 'gpt-5.6-codex',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: {
      sha: 'a'.repeat(40),
      shortSha: 'aaaaaaa',
      message: 'feat: reviewed delivery',
      filesChanged: 1,
      files: ['shared.txt'],
      at: '2026-09-28T19:00:00Z',
    },
    commits: [],
    ownerClientId: 'local-default',
    tags: [],
    integration: {
      status: 'integrated',
      sha: 'aaaaaaa',
      deliveryRef: 'runner/agent-runner-01/' + key,
      integrationBranch: 'develop',
      detail: 'Every attributed commit is reachable from origin/develop.',
      verification,
    },
  };
}

const verified = card('verified-delivery', 'AGT-3101', 'Gate passed on the merged tree', '6-completed', {
  state: 'integrated-verified',
  sha: TREE,
  evidence: 'gate-run',
  gateVerdict: 'Ok',
  gateFailed: false,
  reason: `The gate ran once on the current branch tip ${TREE.slice(0, 7)} and returned Ok.`,
});

const unverified = card('unverified-delivery', 'AGT-3102', 'Pushed to develop without a passing gate', '5-human-review', {
  state: 'integrated-unverified',
  sha: TREE,
  evidence: 'gate-run',
  gateVerdict: 'Fail',
  gateFailed: true,
  reason: `The gate ran once on the current branch tip ${TREE.slice(0, 7)} and returned Fail: 2 tests failed.`,
});

function json(route: Route, body: unknown) {
  return route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(body),
  });
}

const GROUPED = {
  backlog: [],
  preparation: [],
  orchestratorPrep: [],
  ready: [],
  progress: [],
  failedPickup: [],
  codeNotComplete: [],
  review: [],
  autoReview: [],
  humanReview: [unverified],
  escalated: [],
  completed: [verified],
  archive: [],
};

async function installRoutes(page: Page): Promise<void> {
  await page.route('**/api/**', route => {
    const url = route.request().url();
    // The board reads auth, lanes and runner state through the Studio Core
    // BFF; the legacy routes below stay for the remaining board widgets.
    if (url.includes('/api/v1/studio/auth/status')) {
      return json(route, { profile: 'local', bootstrapRequired: false, authenticated: true, user: null });
    }
    if (url.includes('/api/v1/studio/board')) return json(route, GROUPED);
    if (url.includes('/api/v1/studio/runner/status')) return json(route, { projects: {} });
    if (url.includes('/api/tasks/archive')) {
      return json(route, { items: [], total: 0, offset: 0, limit: 50 });
    }
    if (url.includes('/api/cli/quota')) {
      return json(route, { at: '2026-09-29T06:00:00Z', ttlSeconds: 600, snapshots: [] });
    }
    if (url.includes('/api/cli/usage')) {
      return json(route, { at: '2026-09-29T06:00:00Z', sections: [] });
    }
    if (url.includes('/api/orchestrator/global')) {
      return json(route, { session: null });
    }
    if (url.includes('/api/tasks/grouped')) return json(route, GROUPED);
    if (/\/api\/tasks(\?|$)/.test(url)) return json(route, [unverified, verified]);
    if (url.includes('/api/watch-paths')) {
      return json(route, [{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH }]);
    }
    if (url.includes('/api/runner/status')) return json(route, { projects: {} });
    if (url.includes('/api/environment')) return json(route, { isDev: false, devTools: {} });
    if (url.includes('/api/clients') || url.includes('/api/tags') || url.includes('/api/git/summary')) {
      return json(route, []);
    }
    return json(route, []);
  });

  await page.route('**/api/auth/status', route => json(route, {
    profile: 'local',
    bootstrapRequired: false,
    authenticated: true,
    user: null,
  }));
}

test('the board badge tells integrated-verified from integrated-unverified cards', async ({ page }) => {
  test.setTimeout(60_000);
  await page.addInitScript(() => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'board', projectName: '__all__' }],
      activeKey: 'board:__all__',
    }));
  });

  await installRoutes(page);
  await page.goto('/?includeFixtures=true', { waitUntil: 'domcontentloaded', timeout: 30_000 });

  const unverifiedCard = page.locator('[data-testid="task-card"]', { hasText: 'Pushed to develop without a passing gate' });
  await expect(unverifiedCard).toBeVisible();
  const unverifiedBadge = unverifiedCard.getByTestId('integration-status-badge');
  await expect(unverifiedBadge).toContainText('merged @aaaaaaa · unverified');
  await expect(unverifiedBadge).toHaveAttribute('data-integration-verification', 'integrated-unverified');
  await expect(unverifiedBadge).toHaveAttribute('data-kind', 'unverified');

  const verifiedCard = page.locator('[data-testid="task-card"]', { hasText: 'Gate passed on the merged tree' });
  await expect(verifiedCard).toBeVisible();
  const verifiedBadge = verifiedCard.getByTestId('integration-status-badge');
  await expect(verifiedBadge).toContainText('merged @aaaaaaa · verified');
  await expect(verifiedBadge).toHaveAttribute('data-integration-verification', 'integrated-verified');
  await expect(verifiedBadge).toHaveAttribute('data-kind', 'integrated');

  await dismissDevErrorDialog(page);
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await unverifiedCard.screenshot({
      path: path.join(RESULTS_DIR, `integration-verification-badge--unverified--${theme}--mocked.png`),
    });
    await verifiedCard.screenshot({
      path: path.join(RESULTS_DIR, `integration-verification-badge--verified--${theme}--mocked.png`),
    });
  }
});
