import { expect, test, type Page, type Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

const PROJECT = 'Agent Studio';
const WATCH_PATH = '/fixtures/agent-studio';
const RESULTS = process.env['JOB_RESULTS_DIR']?.trim()
  ? resolve(process.env['JOB_RESULTS_DIR'])
  : resolve('test-results', 'multi-repository-delivery');

function commits(count: number, prefix: string, release = true) {
  return Array.from({ length: count }, (_, index) => ({
    sha: `${prefix}${index}`.padEnd(40, '0'),
    onIntegrationBranch: true,
    onReleaseBranch: release,
  }));
}

const repositories = [
  ['agent-studio', 5, 'a', 'develop'],
  ['runner', 4, 'b', 'main'],
  ['token-economy', 4, 'c', 'main'],
  ['chat', 3, 'd', 'main'],
  ['ai-patterns.dev', 2, 'e', 'main'],
  ['quality-studio', 1, 'f', 'main'],
  ['.github', 1, '9', 'main'],
] as const;

function attributedCommits(repository: string, count: number, prefix: string, branch: string) {
  return Array.from({ length: count }, (_, index) => {
    const sha = `${prefix}${index}`.padEnd(40, '0');
    return {
      sha,
      shortSha: sha.slice(0, 7),
      message: `[${repository}] feat: externalized delivery ${index + 1}`,
      repository,
      branch,
      filesChanged: 1,
      files: [`${repository}/delivery-${index + 1}.txt`],
      at: `2026-08-04T08:${String(index).padStart(2, '0')}:00Z`,
      attribution: 'automatic',
      confidence: 1,
    };
  });
}

const task = {
  id: 'externalization-sweep',
  taskKey: `${WATCH_PATH}::externalization-sweep`,
  key: 'AGT-2307',
  title: 'Externalization sweep',
  state: '5-human-review',
  order: 1,
  agent: 'codex',
  cliType: 'codex',
  createdAt: '2026-08-04T07:00:00Z',
  lastActivity: '2026-08-04T08:05:00Z',
  watchPath: WATCH_PATH,
  projectName: PROJECT,
  folderPath: `${WATCH_PATH}/5-human-review/externalization-sweep`,
  execution: null,
  commit: null,
  commits: repositories.flatMap(([repository, count, prefix, branch]) =>
    attributedCommits(repository, count, prefix, branch),
  ),
  ownerClientId: 'local-default',
  tags: [],
  integration: {
    status: 'integrated',
    sha: 'a230700',
    integrationBranch: 'develop',
    detail: '20/20 attributed commits integrated in seven repositories.',
    repositories: repositories.map(([repository, count, prefix, branch]) => ({
      repository,
      commits: commits(count, prefix),
      integrationBranch: branch,
      releaseBranch: 'main',
      onIntegrationBranch: true,
      onReleaseBranch: true,
      detail:
        branch === 'main' ? `${count}/${count} on main.` : `${count}/${count} on develop and main.`,
    })),
  },
};

function json(route: Route, body: unknown) {
  return route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(body),
  });
}

async function boot(page: Page): Promise<void> {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.addInitScript(() => {
    localStorage.setItem(
      'atp.studio.tabs.v1',
      JSON.stringify({
        v: 1,
        tabs: [{ kind: 'board', projectName: '__all__' }],
        activeKey: 'board:__all__',
      }),
    );
  });
  await page.route('**/api/**', (route) => {
    const url = route.request().url();
    if (url.includes('/api/tasks/archive')) return json(route, { items: [], total: 0 });
    if (url.includes('/api/auth/status')) {
      return json(route, {
        profile: 'local',
        bootstrapRequired: false,
        authenticated: true,
        user: null,
      });
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
        humanReview: [task],
        escalated: [],
        completed: [],
        archive: [],
      });
    }
    if (/\/api\/(?:tasks|jobs)(\?|$)/.test(url)) return json(route, [task]);
    if (url.includes('/api/watch-paths')) {
      return json(route, [
        {
          name: PROJECT,
          path: WATCH_PATH,
          rootPath: WATCH_PATH,
          repositoryPath: WATCH_PATH,
        },
      ]);
    }
    if (url.includes('/api/environment')) return json(route, { isDev: false, devTools: {} });
    if (url.includes('/api/runner/status')) return json(route, { projects: {} });
    if (url.includes('/api/clients')) {
      return json(route, [{ id: 'local-default', displayName: 'Local', kind: 'agent-instance' }]);
    }
    if (url.includes('/api/cli/')) return json(route, { snapshots: [], sessions: [] });
    return json(route, []);
  });
  await page.goto('/?includeFixtures=true');
  await expect(page.getByTestId('task-card').filter({ hasText: task.title }).first()).toBeVisible();
  await dismissDevErrorDialog(page);
  await expect(page.getByText('Unexpected application error')).toHaveCount(0);
}

test.describe('multi-repository delivery status', () => {
  for (const theme of ['light', 'dark'] as const) {
    test(`shows repository-specific integration evidence (${theme})`, async ({
      page,
    }, testInfo) => {
      await boot(page);
      await setTheme(page, theme);

      const card = page.getByTestId('task-card').filter({ hasText: task.title }).first();
      const badge = card.getByTestId('integration-status-badge');
      await expect(card).toBeVisible();
      await expect(badge).toHaveAttribute('data-integration-status', 'integrated');
      await expect(badge).toContainText('agent-studio 5/5 develop and main');
      await expect(badge).toContainText('runner 4/4 main');
      await expect(badge).toContainText('.github 1/1 main');

      mkdirSync(RESULTS, { recursive: true });
      const screenshot = join(RESULTS, `multi-repository-delivery--mocked-${theme}.png`);
      await page.screenshot({ path: screenshot, fullPage: false });
      await testInfo.attach(`multi-repository-delivery-${theme}.png`, {
        path: screenshot,
        contentType: 'image/png',
      });
    });
  }
});
