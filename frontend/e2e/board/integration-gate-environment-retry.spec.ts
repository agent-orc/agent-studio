import { test, expect } from '../fixtures/dev-backend';
import type { Page, Route } from '@playwright/test';
import { setTheme } from '../helpers/theme';

// AGT-2824: a reviewed delivery whose merge gate failed with a gate environment
// failure is retried automatically on a bounded backoff. The card also carries
// the explicit operator action, so a repaired gate host never costs a whole new
// remote review round.

const PROJECT = 'Gate environment retry';
const WATCH_PATH = '/fixtures/gate-environment-retry';
const GATE_REASON =
  "Tool 'node' version v24.18.0 does not match .nvmrc; the build/test gate failed "
  + 'before verification reached test discovery.';

function gateEnvironmentTask(integrated: boolean) {
  return {
    id: 'healed-gate-delivery',
    taskKey: `${WATCH_PATH}::healed-gate-delivery`,
    key: 'AGT-2811',
    title: 'Reviewed delivery blocked by a gate environment failure',
    state: '5-human-review',
    order: 1,
    agent: 'codex',
    cliType: 'codex',
    createdAt: '2026-09-15T08:00:00Z',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/5-human-review/healed-gate-delivery`,
    lastActivity: '2026-09-15T09:30:00Z',
    sessionName: null,
    model: 'gpt-5.6-codex',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: {
      sha: 'b'.repeat(40),
      shortSha: 'bbbbbbb',
      message: 'feat: reviewed delivery',
      filesChanged: 1,
      files: ['gate.txt'],
      at: '2026-09-15T09:00:00Z',
    },
    commits: [],
    ownerClientId: 'local-default',
    tags: [],
    integration: integrated
      ? {
        status: 'integrated',
        sha: 'bbbbbbb',
        integrationBranch: 'develop',
        detail: 'All attributed commits are on develop.',
      }
      : {
        status: 'pending',
        sha: null,
        integrationBranch: 'develop',
        detail: `gate environment: ${GATE_REASON}`,
        failure: {
          code: 'gate-environment-failure',
          label: 'Gate environment failure',
          reason: GATE_REASON,
          rebaseRecoveryAvailable: false,
        },
      },
  };
}

function conflictTask() {
  return {
    ...gateEnvironmentTask(false),
    id: 'conflicted-delivery',
    taskKey: `${WATCH_PATH}::conflicted-delivery`,
    key: 'AGT-2227',
    title: 'Delivery that conflicts with develop',
    folderPath: `${WATCH_PATH}/5-human-review/conflicted-delivery`,
    integration: {
      status: 'conflict-skipped',
      sha: null,
      integrationBranch: 'develop',
      detail: 'Conflicted: shared.txt.',
      failure: {
        code: 'merge-conflict',
        label: 'Merge conflict',
        reason: 'The delivery conflicts with the current integration branch.',
        rebaseRecoveryAvailable: true,
      },
    },
  };
}

function json(route: Route, body: unknown) {
  return route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(body),
  });
}

async function installRoutes(
  page: Page,
  integrated: () => boolean,
  waitForRetry: Promise<void>,
): Promise<void> {
  await page.route('**/api/**', route => {
    const url = route.request().url();
    const current = gateEnvironmentTask(integrated());
    const conflicted = conflictTask();
    if (url.includes('/api/tasks/archive')) {
      return json(route, { items: [], total: 0, offset: 0, limit: 50 });
    }
    if (url.includes('/api/cli/quota')) {
      return json(route, { at: '2026-09-15T10:00:00Z', ttlSeconds: 600, snapshots: [] });
    }
    if (url.includes('/api/cli/usage')) {
      return json(route, { at: '2026-09-15T10:00:00Z', sections: [] });
    }
    if (url.includes('/api/orchestrator/global')) return json(route, { session: null });
    if (url.includes('/api/tasks/grouped')) {
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
        humanReview: [current, conflicted],
        escalated: [],
        completed: [],
        archive: [],
      });
    }
    if (/\/api\/tasks(\?|$)/.test(url)) return json(route, [current, conflicted]);
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

  await page.route('**/api/tasks/healed-gate-delivery/integration/retry**', async route => {
    await waitForRetry;
    await route.fulfill({
      status: 202,
      contentType: 'application/json',
      body: JSON.stringify({
        status: 'integrated',
        integrated: true,
        attempt: 2,
        maxAttempts: 3,
        outcome: 'Merged',
        reviewReused: true,
      }),
    });
  });
}

test('gate environment card retries the integration without a new review', async ({ page, devBackend }, testInfo) => {
  test.setTimeout(120_000);
  void devBackend;
  await page.addInitScript(() => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'board', projectName: '__all__' }],
      activeKey: 'board:__all__',
    }));
  });

  let integrated = false;
  let releaseRetry!: () => void;
  const retryGate = new Promise<void>(resolve => { releaseRetry = resolve; });
  await installRoutes(page, () => integrated, retryGate);
  await page.goto('/?includeFixtures=true', { waitUntil: 'domcontentloaded', timeout: 30_000 });

  const card = page.locator('[data-testid="task-card"]', {
    hasText: 'Reviewed delivery blocked by a gate environment failure',
  });
  await expect(card).toBeVisible();
  const badge = card.getByTestId('integration-status-badge');
  // CAC-18: never a conflict, never "teilweise integriert" - the card stays
  // honestly NOT integrated with the gate environment code attached.
  await expect(badge).toHaveAttribute('data-integration-status', 'pending');
  await expect(badge).toHaveAttribute(
    'data-integration-failure-code',
    'gate-environment-failure',
  );

  const retry = card.getByTestId('task-card-integration-retry');
  await expect(retry).toHaveAccessibleName(/retry the integration/i);
  // The environment failure is not rebase-recoverable, so the steer-round
  // action must not be offered next to it.
  await expect(card.getByTestId('task-card-integration-recovery')).toHaveCount(0);

  const conflicted = page.locator('[data-testid="task-card"]', {
    hasText: 'Delivery that conflicts with develop',
  });
  await expect(conflicted.getByTestId('task-card-integration-recovery')).toHaveCount(1);
  await expect(conflicted.getByTestId('task-card-integration-retry')).toHaveCount(0);

  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    const path = testInfo.outputPath(`integration-gate-environment-retry--${theme}--mocked.png`);
    await card.screenshot({ path });
    await testInfo.attach(`integration-gate-environment-retry--${theme}--mocked.png`, {
      path,
      contentType: 'image/png',
    });
  }

  const retryRequest = page.waitForRequest(request =>
    request.method() === 'POST'
    && new URL(request.url()).pathname === '/api/tasks/healed-gate-delivery/integration/retry'
    && new URL(request.url()).searchParams.get('watchPath') === WATCH_PATH,
  );
  await retry.click();
  await retryRequest;
  await expect(retry).toHaveAttribute('aria-busy', 'true');

  integrated = true;
  releaseRetry();
  await expect(page.getByText(/no new review was needed/i)).toBeVisible();
  await expect(page.locator('[data-testid="task-card"]', {
    hasText: 'Reviewed delivery blocked by a gate environment failure',
  }).getByTestId('task-card-integration-retry')).toHaveCount(0);
});
