import { test, expect } from '../fixtures/dev-backend';
import type { Page, Route } from '@playwright/test';
import { setTheme } from '../helpers/theme';

const PROJECT = 'Integration recovery';
const WATCH_PATH = '/fixtures/integration-recovery';

function task(queued: boolean) {
  return {
    id: 'conflicted-delivery',
    taskKey: `${WATCH_PATH}::conflicted-delivery`,
    key: 'AGT-2227',
    title: 'Accepted delivery with merge conflict',
    state: queued ? '2-ready' : '6-completed',
    order: 1,
    agent: 'codex',
    cliType: 'codex',
    createdAt: '2026-07-24T18:00:00Z',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/${queued ? '2-ready' : '6-completed'}/conflicted-delivery`,
    lastActivity: '2026-07-24T20:00:00Z',
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
      at: '2026-07-24T19:00:00Z',
    },
    commits: [],
    ownerClientId: 'local-default',
    tags: queued ? [] : ['integrationpending'],
    integration: queued ? null : {
      status: 'conflict-skipped',
      sha: null,
      integrationBranch: 'develop',
      detail: 'Conflicted: shared.txt. Start the integration recovery action to run a steer round.',
      failure: {
        code: 'merge-conflict',
        label: 'Merge conflict',
        reason: 'Conflicted: shared.txt. Rebase the delivery onto the current integration branch.',
        rebaseRecoveryAvailable: true,
      },
    },
  };
}

function noTaskBranchTask() {
  const base = task(false);
  return {
    ...base,
    id: 'missing-delivery-branch',
    taskKey: `${WATCH_PATH}::missing-delivery-branch`,
    key: 'AGT-2531',
    title: 'Returned after NoTaskBranch',
    state: '5-human-review',
    folderPath: `${WATCH_PATH}/5-human-review/missing-delivery-branch`,
    tags: ['integrationpending'],
    integration: {
      status: 'conflict-skipped',
      sha: null,
      integrationBranch: 'develop',
      detail: 'No task branch to merge.',
      failure: {
        code: 'no-task-branch',
        label: 'No task branch',
        reason: 'No task branch to merge.',
        rebaseRecoveryAvailable: false,
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
  queued: () => boolean,
  waitForRecovery: Promise<void>,
): Promise<void> {
  await page.route('**/api/**', route => {
    const url = route.request().url();
    const current = task(queued());
    const missingBranch = noTaskBranchTask();
    if (url.includes('/api/tasks/archive')) {
      return json(route, { items: [], total: 0, offset: 0, limit: 50 });
    }
    if (url.includes('/api/cli/quota')) {
      return json(route, { at: '2026-07-24T20:00:00Z', ttlSeconds: 600, snapshots: [] });
    }
    if (url.includes('/api/cli/usage')) {
      return json(route, { at: '2026-07-24T20:00:00Z', sections: [] });
    }
    if (url.includes('/api/orchestrator/global')) {
      return json(route, { session: null });
    }
    if (url.includes('/api/v1/studio/board')) {
      return json(route, {
        backlog: [],
        preparation: [],
        orchestratorPrep: [],
        ready: queued() ? [current] : [],
        progress: [],
        failedPickup: [],
        codeNotComplete: [],
        review: [],
        autoReview: [],
        humanReview: [missingBranch],
        escalated: [],
        completed: queued() ? [] : [current],
        archive: [],
      });
    }
    if (/\/api\/tasks(\?|$)/.test(url)) return json(route, [current, missingBranch]);
    if (url.includes('/api/watch-paths')) {
      return json(route, [{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH }]);
    }
    if (url.includes('/api/v1/studio/runner/status')) return json(route, { projects: {} });
    if (url.includes('/api/environment')) return json(route, { isDev: false, devTools: {} });
    if (url.includes('/api/clients') || url.includes('/api/tags') || url.includes('/api/git/summary')) {
      return json(route, []);
    }
    return json(route, []);
  });

  await page.route('**/api/v1/studio/auth/status', route => json(route, {
    profile: 'local',
    bootstrapRequired: false,
    authenticated: true,
    user: null,
  }));

  await page.route('**/api/tasks/conflicted-delivery/integration/rebase**', async route => {
    await waitForRecovery;
    await route.fulfill({
      status: 202,
      contentType: 'application/json',
      body: JSON.stringify({
        status: 'queued',
        mode: 'steer',
        targetState: '2-ready',
        position: 0,
        deliveryRef: 'runner/agent-runner-01/AGT-2227',
        resultSha: 'a'.repeat(40),
        integrationBranch: 'develop',
      }),
    });
  });
}

test('conflict card queues the focused rebase steer round', async ({ page, devBackend }, testInfo) => {
  test.setTimeout(120_000);
  void devBackend;
  await page.addInitScript(() => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'board', projectName: '__all__' }],
      activeKey: 'board:__all__',
    }));
  });

  let queued = false;
  let releaseRecovery!: () => void;
  const recoveryGate = new Promise<void>(resolve => { releaseRecovery = resolve; });
  await installRoutes(page, () => queued, recoveryGate);
  await page.goto('/?includeFixtures=true', { waitUntil: 'domcontentloaded', timeout: 30_000 });

  const card = page.locator('[data-testid="task-card"]', {
    hasText: 'Accepted delivery with merge conflict',
  });
  await expect(card).toBeVisible();
  await expect(card.getByTestId('integration-status-badge')).toContainText('Merge conflict');
  await expect(card.getByTestId('integration-status-badge'))
    .toHaveAttribute('data-integration-failure-code', 'merge-conflict');
  const action = card.getByTestId('task-card-integration-recovery');
  await expect(action).toHaveAccessibleName(/queue a steer round/i);

  const missingBranch = page.locator('[data-testid="task-card"]', {
    hasText: 'Returned after NoTaskBranch',
  });
  await expect(missingBranch).toBeVisible();
  await expect(missingBranch.getByTestId('integration-status-badge')).toContainText('No task branch');
  await expect(missingBranch.getByTestId('integration-status-badge'))
    .toHaveAttribute('data-integration-failure-code', 'no-task-branch');
  await expect(missingBranch.getByTestId('task-card-integration-recovery')).toHaveCount(0);

  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    const path = testInfo.outputPath(`integration-conflict-recovery--${theme}--mocked.png`);
    await card.screenshot({ path });
    await testInfo.attach(`integration-conflict-recovery--${theme}--mocked.png`, {
      path,
      contentType: 'image/png',
    });
    const missingBranchPath = testInfo.outputPath(`integration-no-task-branch--${theme}--mocked.png`);
    await missingBranch.screenshot({ path: missingBranchPath });
    await testInfo.attach(`integration-no-task-branch--${theme}--mocked.png`, {
      path: missingBranchPath,
      contentType: 'image/png',
    });
  }

  const recoveryRequest = page.waitForRequest(request =>
    request.method() === 'POST'
    && new URL(request.url()).pathname === '/api/tasks/conflicted-delivery/integration/rebase'
    && new URL(request.url()).searchParams.get('watchPath') === WATCH_PATH,
  );
  await action.click();
  await recoveryRequest;
  await expect(action).toHaveAttribute('aria-busy', 'true');

  queued = true;
  releaseRecovery();
  await expect(page.getByText(/integration recovery queued/i)).toBeVisible();
  await expect(page.locator('[data-testid="task-card"]', {
    hasText: 'Accepted delivery with merge conflict',
  })).toHaveCount(1);
  await expect(page.getByTestId('task-card-integration-recovery')).toHaveCount(0);
});
