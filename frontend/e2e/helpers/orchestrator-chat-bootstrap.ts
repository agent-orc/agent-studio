import type { Page, Route } from '@playwright/test';

const EMPTY_GROUPED_TASKS = {
  backlog: [],
  preparation: [],
  orchestratorPrep: [],
  ready: [],
  progress: [],
  failedPickup: [],
  autoReview: [],
  humanReview: [],
  review: [],
  completed: [],
  archive: [],
};

function fulfillJson(route: Route, body: unknown) {
  return route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(body),
  });
}

/**
 * Isolate composer regressions from operator backend state. The focused chat
 * specs install their own chat POST and attachment routes after this bootstrap
 * route, while every unrelated shell request receives a stable empty shape.
 */
export async function installOrchestratorChatBootstrap(
  page: Page,
  project = 'composer-fixture',
): Promise<void> {
  await page.route('**/api/**', async route => {
    const request = route.request();
    const pathname = new URL(request.url()).pathname;

    if (request.method() !== 'GET') {
      await route.fallback();
      return;
    }

    if (pathname === '/api/v1/studio/auth/status') {
      await fulfillJson(route, {
        profile: 'local',
        bootstrapRequired: false,
        authenticated: false,
        user: null,
      });
      return;
    }
    if (pathname === '/api/watch-paths') {
      await fulfillJson(route, [{
        name: project,
        path: `/tmp/${project}`,
        rootPath: `/tmp/${project}`,
        repositoryPath: '',
      }]);
      return;
    }
    if (pathname === '/api/v1/workspaces') {
      await fulfillJson(route, [{
        id: 'workspace-composer-fixture',
        displayName: 'Composer fixture',
        sortOrder: 0,
        isDefault: true,
        projects: [{
          id: project,
          displayName: project,
          shortCode: 'CF',
          workspaceId: 'workspace-composer-fixture',
          storageLocation: `/tmp/${project}`,
          archived: false,
          urls: [],
        }],
      }]);
      return;
    }
    if (pathname === '/api/tasks' || pathname === '/api/v1/projects') {
      await fulfillJson(route, []);
      return;
    }
    if (pathname === '/api/v1/studio/board') {
      await fulfillJson(route, EMPTY_GROUPED_TASKS);
      return;
    }
    if (pathname === '/api/tasks/archive') {
      await fulfillJson(route, { items: [], total: 0, offset: 0, limit: 50, hasMore: false });
      return;
    }
    if (pathname === '/api/crash-recovery/pending') {
      await fulfillJson(route, { pending: [] });
      return;
    }
    if (pathname === '/api/v1/studio/runner/status') {
      await fulfillJson(route, { projects: {} });
      return;
    }
    if (pathname === '/api/auto-review/status') {
      await fulfillJson(route, {
        lastTickAt: null,
        accept: 0,
        reissue: 0,
        escalate: 0,
        aspectsRun: 0,
        pending: 0,
        currentJob: null,
        currentProject: null,
        activeJobs: [],
      });
      return;
    }
    if (pathname === '/api/v1/studio/orchestrator/sessions') {
      await fulfillJson(route, { sessions: [] });
      return;
    }
    if (pathname === '/api/cli/quota') {
      await fulfillJson(route, { at: '2026-01-01T00:00:00Z', snapshots: [], ttlSeconds: 600 });
      return;
    }
    if (/^\/api\/cli\/[^/]+\/models$/.test(pathname)) {
      await fulfillJson(route, { models: [], source: 'fixture' });
      return;
    }
    if (
      /^\/api\/v1\/studio\/runner\/[^/]+\/orchestrator-chat$/.test(pathname)
      || /^\/api\/runner\/[^/]+\/orchestrator-chat$/.test(pathname)
    ) {
      await fulfillJson(route, { project, turns: [] });
      return;
    }
    if (
      pathname === '/api/environment'
      || pathname === '/api/projects/settings'
      || /^\/api\/clients\/[^/]+\/defaults$/.test(pathname)
    ) {
      await fulfillJson(route, {});
      return;
    }
    if (
      pathname === '/api/tags'
      || pathname === '/api/clients'
      || pathname === '/api/clients/'
      || pathname === '/api/git/summary'
      || pathname === '/api/v1/management/remote-hosts'
      || pathname.startsWith('/api/bus/')
    ) {
      await fulfillJson(route, []);
      return;
    }

    await fulfillJson(route, {});
  });
}
