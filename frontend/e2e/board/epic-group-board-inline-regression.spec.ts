import { test, expect, type Page } from '@playwright/test';

const PROJECT = 'Epic Regression';
const WATCH_PATH = 'C:/fixtures/epic-inline-regression';

function makeJob(id: string, title: string, state: string, order: number, extra: Record<string, unknown> = {}) {
  return {
    id,
    taskKey: `${WATCH_PATH}::${id}`,
    title,
    state,
    order,
    agent: 'codex',
    cliType: 'codex',
    createdAt: '2026-06-06T08:00:00Z',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/.orchestrator/jobs/${state}/${id}`,
    lastActivity: '2026-06-06T08:30:00Z',
    sessionName: null,
    model: null,
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    commits: [],
    ownerClientId: 'local-default',
    ...extra,
  };
}

const EPIC = makeJob('e2e-epic-inline', 'Inline epic rollup', '2-ready', 1, { kind: 'epic' });
const SUB_READY = makeJob('e2e-epic-inline-sub-ready', 'Ready sub-task', '2-ready', 1, {
  epicId: EPIC.id,
});
const SUB_REVIEW = makeJob('e2e-epic-inline-sub-review', 'Review sub-task', '5-human-review', 2, {
  epicId: EPIC.id,
  orchestratorVerdict: 'escalate',
});

const GROUPED_PAYLOAD = {
  backlog: [],
  preparation: [],
  orchestratorPrep: [],
  ready: [EPIC, SUB_READY],
  progress: [],
  failedPickup: [],
  codeNotComplete: [],
  review: [],
  autoReview: [],
  humanReview: [SUB_REVIEW],
  completed: [],
  archive: [],
};

const EPIC_DETAIL = {
  info: EPIC,
  promptMarkdown: '# Inline epic rollup',
  statusMarkdown: null,
  contextUsage: null,
  log: [],
  promptHistory: [],
  titleHistory: [],
  reviewEvidence: [],
  summaryState: null,
};

async function installRoutes(page: Page) {
  await page.route('**/api/**', (route) => {
    const url = route.request().url();
    const json = (body: unknown) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });

    if (url.includes('/api/auth/status')) {
      return json({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null });
    }
    if (url.includes('/api/tasks/archive')) {
      return json({ items: [], total: 0, offset: 0, limit: 50 });
    }
    if (url.includes('/api/epics/completed/count')) return json({ count: 0 });
    if (url.includes('/api/tasks/grouped')) return json(GROUPED_PAYLOAD);
    if (url.includes(`/api/tasks/${EPIC.id}`)) return json(EPIC_DETAIL);
    if (url.includes(`/api/epics/${EPIC.id}`)) {
      return json({
        ...EPIC,
        subTaskTotal: 2,
        completed: 0,
        inProgress: 0,
        open: 2,
        byState: { '2-ready': 1, '5-human-review': 1 },
        subTasks: [SUB_READY, SUB_REVIEW],
      });
    }
    if (/\/api\/tasks(\?|$)/.test(url)) {
      return json([EPIC, SUB_READY, SUB_REVIEW]);
    }
    if (url.includes('/api/watch-paths')) {
      return json([{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH }]);
    }
    if (url.includes('/api/runner/status')) {
      return json({ projects: { [PROJECT]: { projectName: PROJECT, mode: 'manual', activeJobId: null, activeExecution: null, queuedJobIds: [] } } });
    }
    if (url.includes('/api/environment')) {
      return json({ isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } });
    }
    if (url.includes('/api/git/summary')) return json([]);
    if (url.includes('/api/git/hygiene')) return json({});
    if (url.includes('/api/cli/quota')) return json({ snapshots: [], ttlSeconds: 600 });
    if (url.includes('/api/cli/usage')) return json({ items: [] });
    if (url.includes('/api/agent-rules') || url.includes('/api/clients') || url.includes('/api/tags')) return json([]);

    return json([]);
  });
}

test.describe('Board: epic group inline sub-tasks regression', () => {
  test('epic sections open with inline sub-tasks and can collapse again', async ({ page }, testInfo) => {
    await installRoutes(page);
    await page.goto('/?includeFixtures=true');

    const toggle = page.getByTestId('studio-board-epic-toggle');
    await expect(toggle).toBeVisible({ timeout: 15_000 });
    await toggle.click();

    const group = page.getByTestId(`epic-group-${EPIC.id}`);
    await expect(group).toBeVisible({ timeout: 10_000 });
    await expect(group.getByTestId(`epic-group-subtasks-${EPIC.id}`)).toBeVisible();
    await expect(group.getByText('Ready sub-task')).toBeVisible();
    await expect(group.getByText('Review sub-task')).toBeVisible();
    await expect(group.getByTestId('epic-group-subtask-verdict')).toHaveText('escalate');

    const expandedShot = testInfo.outputPath('epic-group-expanded-inline--mocked.png');
    await group.screenshot({ path: expandedShot });
    await testInfo.attach('epic-group-expanded-inline', { path: expandedShot, contentType: 'image/png' });

    await group.getByTestId(`epic-group-collapse-${EPIC.id}`).click();
    await expect(group.getByTestId(`epic-group-subtasks-${EPIC.id}`)).toHaveCount(0);

    const collapsedShot = testInfo.outputPath('epic-group-collapsed--mocked.png');
    await group.screenshot({ path: collapsedShot });
    await testInfo.attach('epic-group-collapsed', { path: collapsedShot, contentType: 'image/png' });
  });

  test('opens an epic detail after the lazy task-detail chunk loads', async ({ page }) => {
    await installRoutes(page);
    await page.goto('/?includeFixtures=true');

    await page.getByTestId('studio-board-epic-toggle').click();
    const group = page.getByTestId(`epic-group-${EPIC.id}`);
    await expect(group).toBeVisible({ timeout: 10_000 });
    await group.locator('app-job-card').click();

    await expect(page.getByTestId('studio-epic')).toBeVisible({ timeout: 15_000 });
    await expect(page.getByTestId('studio-epic-detail')).toBeVisible({ timeout: 15_000 });
  });
});
