import { expect, test, type Page, type Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';

const PROJECT = 'Failure intervention replay';
const WATCH_PATH = '/fixtures/failure-intervention';
const RESULTS = process.env.JOB_RESULTS_DIR ?? join(process.cwd(), 'results', 'AGT-2765');

const origin = task('origin', 'AGT-2707', 'Review model withdrawal replay', '5e-escalated', {
  dependsOn: [], relatedTo: [], blockedBy: ['AGT-2801'], supersedes: [],
  raisedFollowUps: ['AGT-2801'], followUpOf: [], workbenches: [],
});
const followUp = {
  ...task('follow-up', 'AGT-2801', 'Intervention: review toolchain unavailable', '1-preparation', {
    dependsOn: [], relatedTo: [], blockedBy: [], supersedes: [],
    raisedFollowUps: [], followUpOf: ['AGT-2707', 'AGT-2755'], workbenches: [],
  }),
  creationSource: 'orchestrator',
  createdBy: 'Orchestrator',
};

function task(id: string, key: string, title: string, state: string, references: object) {
  return {
    id, taskKey: `${WATCH_PATH}::${id}`, key, title, state, references,
    order: 1, agent: 'codex', cliType: 'codex', model: 'gpt-5.6-codex',
    createdAt: '2026-09-11T06:20:00Z', lastActivity: '2026-09-11T06:20:00Z',
    watchPath: WATCH_PATH, projectName: PROJECT, folderPath: `${WATCH_PATH}/${state}/${id}`,
    execution: null, commit: null, commits: [], ownerClientId: 'local-default', tags: [],
  };
}

function json(route: Route, body: unknown) {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

async function installRoutes(page: Page): Promise<void> {
  await page.route('**/api/**', route => {
    const url = route.request().url();
    if (url.includes('/api/auth/status')) {
      return json(route, { profile: 'local', bootstrapRequired: false, authenticated: true, user: null });
    }
    if (url.includes('/api/tasks/grouped')) {
      return json(route, {
        backlog: [], preparation: [followUp], orchestratorPrep: [], ready: [], progress: [],
        failedPickup: [], codeNotComplete: [], review: [], autoReview: [], humanReview: [],
        escalated: [origin], completed: [], archive: [],
      });
    }
    if (/\/api\/tasks\/follow-up(?:\?|$)/.test(url)) {
      return json(route, {
        info: followUp,
        promptMarkdown: '# Orchestrator failure intervention',
        promptHistory: [], titleHistory: [], statusMarkdown: null,
        contextUsage: null, log: [], summaryState: null, reviewEvidence: [],
      });
    }
    if (/\/api\/(?:tasks|jobs)(\?|$)/.test(url)) return json(route, [origin, followUp]);
    if (url.includes('/api/watch-paths')) {
      return json(route, [{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH }]);
    }
    if (url.includes('/api/environment')) return json(route, { isDev: false, devTools: {} });
    if (url.includes('/api/runner/status')) return json(route, { projects: {} });
    if (url.includes('/api/clients')) return json(route, []);
    if (url.includes('/api/tasks/reference-status')) return json(route, []);
    if (url.includes('/api/tasks/archive')) return json(route, { items: [], total: 0 });
    if (url.includes('/api/cli/')) return json(route, { snapshots: [], sessions: [] });
    return json(route, []);
  });
}

test('replayed review failure links its origin to an orchestrator-created follow-up', async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.addInitScript(taskKey => {
    localStorage.clear();
    const taskRoute = location.pathname.startsWith('/tasks/');
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: taskRoute
        ? [{ kind: 'task', taskKey }]
        : [{ kind: 'board', projectName: '__all__' }],
      activeKey: taskRoute ? `task:${taskKey}` : 'board:__all__',
    }));
  }, followUp.taskKey);
  await installRoutes(page);
  await page.goto('/?includeFixtures=true');

  const card = page.getByTestId('task-card').filter({ hasText: origin.key }).first();
  const intervention = card.getByTestId('task-card-intervention');
  await expect(intervention).toContainText('AGT-2801 · Preparation · open');

  mkdirSync(RESULTS, { recursive: true });
  const cardShot = join(RESULTS, 'origin-card-intervention-chip--mocked-replay.png');
  await card.screenshot({ path: cardShot });
  await testInfo.attach('origin-card-intervention-chip', { path: cardShot, contentType: 'image/png' });

  await page.goto(`/tasks/${followUp.key}`);
  const attribution = page.getByTestId('overview-created-by-orchestrator');
  await expect(attribution).toContainText('Created by Orchestrator');
  const errorDialog = page.getByTestId('error-dialog-overlay');
  if (await errorDialog.isVisible().catch(() => false)) {
    await errorDialog.click({ position: { x: 4, y: 4 } });
    await expect(errorDialog).toBeHidden();
  }
  const headerShot = join(RESULTS, 'follow-up-header-orchestrator-creator--mocked-replay.png');
  await page.getByTestId('overview-title-block').screenshot({ path: headerShot });
  await testInfo.attach('follow-up-header-orchestrator-creator', { path: headerShot, contentType: 'image/png' });
});
