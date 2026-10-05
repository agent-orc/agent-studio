import { expect, test, type Page, type Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

const PROJECT = 'Badge consistency';
const WATCH_PATH = '/fixtures/stale-status-badges';
const RESULTS = join(process.cwd(), '..', 'results', 'AGT-2416');
const evidenceState = process.env['STALE_BADGE_EVIDENCE_STATE'] === 'before' ? 'before' : 'after';

function execution(id: string, runOutcome: string) {
  return {
    jobId: id,
    taskKey: `${WATCH_PATH}::${id}`,
    processId: 0,
    startedAt: '2026-07-28T08:00:00Z',
    status: 'completed',
    exitCode: 0,
    durationSeconds: 180,
    model: 'gpt-5.6-codex',
    runOutcome,
  };
}

function task(
  id: string,
  title: string,
  state: string,
  overrides: Record<string, unknown> = {},
) {
  return {
    id,
    taskKey: `${WATCH_PATH}::${id}`,
    key: id.toUpperCase(),
    title,
    state,
    order: 1,
    agent: 'codex',
    cliType: 'codex',
    createdAt: '2026-07-28T07:00:00Z',
    lastActivity: '2026-07-28T08:05:00Z',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/${state}/${id}`,
    model: 'gpt-5.6-codex',
    execution: null,
    commit: null,
    commits: [],
    ownerClientId: 'local-default',
    tags: [],
    ...overrides,
  };
}

const staleReview = task('aow-5', 'AOW-5 stale review signals', '5-human-review', {
  orchestratorVerdict: 'escalate',
  execution: execution('aow-5', 'success'),
  outcomeIssue: {
    kind: 'integration-error',
    label: 'Integration error',
    severity: 'High',
    summary: 'Transient integration attempt failed before the successful retry.',
    lastSeenAt: '2026-07-28T08:03:00Z',
  },
  integration: {
    status: 'integrated',
    deliveryRef: 'task/aow-5',
    sha: '2d8d201',
    integrationBranch: 'develop',
    detail: 'Curated retry integrated every attributed commit.',
  },
});

const currentEscalation = task('agt-current', 'Current escalation remains acute', '5e-escalated', {
  orchestratorVerdict: 'escalate',
  execution: execution('agt-current', 'failed'),
  outcomeIssue: {
    kind: 'watchdog-timeout',
    label: 'Watchdog timeout',
    severity: 'High',
    summary: 'The latest run timed out and still needs operator action.',
    lastSeenAt: '2026-07-28T08:04:00Z',
  },
});

const allTasks = [staleReview, currentEscalation];

function json(route: Route, body: unknown) {
  return route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(body),
  });
}

async function installRoutes(page: Page): Promise<void> {
  await page.route('**/api/**', route => {
    const url = route.request().url();
    if (url.includes('/api/tasks/archive')) return json(route, { items: [], total: 0 });
    if (url.includes('/api/v1/studio/auth/status')) {
      return json(route, {
        profile: 'local',
        bootstrapRequired: false,
        authenticated: true,
        user: null,
      });
    }
    if (url.includes('/api/v1/studio/board')) {
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
        humanReview: [staleReview],
        escalated: [currentEscalation],
        completed: [],
        archive: [],
      });
    }
    if (/\/api\/(?:tasks|jobs)(\?|$)/.test(url)) return json(route, allTasks);
    if (url.includes('/api/watch-paths')) {
      return json(route, [{
        name: PROJECT,
        path: WATCH_PATH,
        rootPath: WATCH_PATH,
        repositoryPath: WATCH_PATH,
      }]);
    }
    if (url.includes('/api/environment')) {
      return json(route, { isDev: false, devTools: {} });
    }
    if (url.includes('/api/v1/studio/runner/status')) return json(route, { projects: {} });
    if (url.includes('/api/clients')) {
      return json(route, [{ id: 'local-default', displayName: 'Local', kind: 'agent-instance' }]);
    }
    if (url.includes('/api/cli/')) return json(route, { snapshots: [], sessions: [] });
    return json(route, []);
  });
}

async function boot(page: Page): Promise<void> {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.addInitScript(() => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'board', projectName: '__all__' }],
      activeKey: 'board:__all__',
    }));
  });
  await installRoutes(page);
  await page.goto('/?includeFixtures=true');
  await expect(page.getByTestId('task-card').filter({ hasText: staleReview.title }).first())
    .toBeVisible({ timeout: 15_000 });
  await dismissDevErrorDialog(page);
  await page.evaluate(() => {
    document.querySelector('vite-error-overlay')?.remove();
    document.querySelector('ng-error-overlay')?.remove();
  });
}

function card(page: Page, title: string) {
  return page.getByTestId('task-card').filter({ hasText: title }).first();
}

test.describe('task-card status badges follow current state', () => {
  for (const theme of ['light', 'dark'] as const) {
    test(`integrated Review card drops stale acute signals (${theme})`, async ({ page }, testInfo) => {
      await boot(page);
      await setTheme(page, theme);

      const review = card(page, staleReview.title);
      const escalated = card(page, currentEscalation.title);
      const integration = review.getByTestId('integration-status-badge');
      const staleEscalation = review.getByTestId('task-card-human-review');
      const staleIntegrationError = review.getByTestId('task-card-outcome-issue');

      await expect(integration).toHaveAttribute('data-integration-status', 'integrated');
      await expect(integration).toContainText('merged @2d8d201');

      if (evidenceState === 'before') {
        await expect(staleEscalation).toContainText('Escalated');
        await expect(staleIntegrationError).toContainText('Integration error');
      } else {
        await expect(staleEscalation).toHaveCount(0);
        await expect(staleIntegrationError).toHaveCount(0);
        await expect(review).not.toHaveClass(/task-card--attention/);

        await expect(escalated.getByTestId('task-card-human-review')).toContainText('Escalated');
        await expect(escalated.getByTestId('task-card-outcome-issue')).toContainText('Watchdog timeout');
        await expect(escalated).toHaveClass(/task-card--attention/);
      }

      mkdirSync(RESULTS, { recursive: true });
      const screenshotPath = join(
        RESULTS,
        evidenceState === 'before'
          ? `stale-status-badges--before-${theme}.png`
          : `stale-status-badges--after-${theme}.png`,
      );
      await page.screenshot({ path: screenshotPath, fullPage: false });
      await testInfo.attach(`stale-status-badges--${evidenceState}-${theme}.png`, {
        path: screenshotPath,
        contentType: 'image/png',
      });
    });
  }
});
