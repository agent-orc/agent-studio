import { test, expect } from '../fixtures/dev-backend';
import type { Page, Route } from '@playwright/test';
import { setTheme } from '../helpers/theme';

// AGT-2749: a card whose last integration failure is infrastructure or quota
// (host or provider account fault) is requeued instead of parked, and the
// lane shows the failure class so an operator does not read it as a broken
// product change while the platform is still retrying it.

const PROJECT = 'Infra requeue';
const WATCH_PATH = '/fixtures/infra-requeue';

function infrastructureTask() {
  return {
    id: 'infra-fault-delivery',
    taskKey: `${WATCH_PATH}::infra-fault-delivery`,
    key: 'AGT-2708',
    title: 'Delivery hit a git network timeout',
    state: '5-human-review',
    order: 1,
    agent: 'codex',
    cliType: 'codex',
    createdAt: '2026-09-06T18:00:00Z',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/5-human-review/infra-fault-delivery`,
    lastActivity: '2026-09-06T20:00:00Z',
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
      at: '2026-09-06T19:00:00Z',
    },
    commits: [],
    ownerClientId: 'local-default',
    tags: ['integrationpending'],
    integration: {
      status: 'conflict-skipped',
      sha: null,
      integrationBranch: 'develop',
      detail: "Integration branch 'develop' could not be fetched from origin: git operation timed out after 30 seconds",
      failure: {
        code: 'integration-error',
        label: 'Integration failed',
        reason: "Integration branch 'develop' could not be fetched from origin: git operation timed out after 30 seconds",
        rebaseRecoveryAvailable: false,
        failureClass: 'infrastructure',
        failureSignature: 'git-network-timeout',
      },
    },
  };
}

function quotaTask() {
  const base = infrastructureTask();
  return {
    ...base,
    id: 'quota-fault-delivery',
    taskKey: `${WATCH_PATH}::quota-fault-delivery`,
    key: 'QS-89',
    title: 'Review blocked on an exhausted CLI quota',
    folderPath: `${WATCH_PATH}/5-human-review/quota-fault-delivery`,
    integration: {
      status: 'conflict-skipped',
      sha: null,
      integrationBranch: 'develop',
      detail: 'The CLI provider quota is exhausted.',
      failure: {
        code: 'integration-error',
        label: 'Integration failed',
        reason: 'The CLI provider quota is exhausted.',
        rebaseRecoveryAvailable: false,
        failureClass: 'quota',
        failureSignature: 'cli-quota-exhausted',
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

async function installRoutes(page: Page): Promise<void> {
  const infra = infrastructureTask();
  const quota = quotaTask();
  await page.route('**/api/**', route => {
    const url = route.request().url();
    if (url.includes('/api/tasks/archive')) {
      return json(route, { items: [], total: 0, offset: 0, limit: 50 });
    }
    if (url.includes('/api/cli/quota')) {
      return json(route, { at: '2026-09-06T20:00:00Z', ttlSeconds: 600, snapshots: [] });
    }
    if (url.includes('/api/cli/usage')) {
      return json(route, { at: '2026-09-06T20:00:00Z', sections: [] });
    }
    if (url.includes('/api/orchestrator/global')) {
      return json(route, { session: null });
    }
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
        humanReview: [infra, quota],
        escalated: [],
        completed: [],
        archive: [],
      });
    }
    if (/\/api\/tasks(\?|$)/.test(url)) return json(route, [infra, quota]);
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

test('Human Review cards show the requeue class for infrastructure and quota faults', async ({ page, devBackend }, testInfo) => {
  test.setTimeout(60_000);
  void devBackend;
  await page.addInitScript(() => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'board', projectName: '__all__' }],
      activeKey: 'board:__all__',
    }));
  });

  await installRoutes(page);
  await page.goto('/?includeFixtures=true', { waitUntil: 'domcontentloaded', timeout: 30_000 });

  const infraCard = page.locator('[data-testid="task-card"]', {
    hasText: 'Delivery hit a git network timeout',
  });
  await expect(infraCard).toBeVisible();
  const infraBadge = infraCard.getByTestId('integration-status-badge');
  await expect(infraBadge).toContainText('Integration failed · Infrastructure');
  await expect(infraBadge).toHaveAttribute('data-integration-failure-class', 'infrastructure');
  // No rebase action for a host fault: the delivery is unchanged, only the
  // verification needs to be replayed.
  await expect(infraCard.getByTestId('task-card-integration-recovery')).toHaveCount(0);

  const quotaCard = page.locator('[data-testid="task-card"]', {
    hasText: 'Review blocked on an exhausted CLI quota',
  });
  await expect(quotaCard).toBeVisible();
  const quotaBadge = quotaCard.getByTestId('integration-status-badge');
  await expect(quotaBadge).toContainText('Integration failed · Quota');
  await expect(quotaBadge).toHaveAttribute('data-integration-failure-class', 'quota');

  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    const path = testInfo.outputPath(`integration-failure-class-badge--${theme}--mocked.png`);
    await infraCard.screenshot({ path });
    await testInfo.attach(`integration-failure-class-badge--${theme}--mocked.png`, {
      path,
      contentType: 'image/png',
    });
  }
});
