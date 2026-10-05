import { test, expect, type Page } from '@playwright/test';

/**
 * Board display contract (operator 2026-06-09): the flat lane board shows
 * TASK cards only. Epics are containers, not board work-items, so they must
 * not render as a card in ANY lane (0-backlog, 2-ready, 3-progress,
 * 5-human-review, 5e-escalated, ...). Epics stay reachable through the
 * "Group by epic" / epic navigation view. Mirrors the pickup rule that epics
 * are not pickable.
 *
 * Driven by `excludeEpics` (frontend/.../board/components/epic-grouping.util.ts),
 * wired into `App.displayGrouped`. Fully mocked via route interception so it
 * runs against any served frontend without a real backend. Targets the dev
 * build (`/api/tasks/grouped`, `data-testid="task-card"`).
 */

const PROJECT = 'fixture-hide-epics';
const WATCH_PATH = 'C:/fixtures/hide-epics-repo';

function makeCard(id: string, state: string, title: string, order: number, kind: 'task' | 'epic') {
  return {
    id,
    taskKey: `${WATCH_PATH}::${id}`,
    title,
    state,
    order,
    agent: 'claude',
    cliType: 'claude',
    createdAt: '2026-06-09T09:00:00Z',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/.orchestrator/tasks/${state}/${id}`,
    lastActivity: '2026-06-09T11:00:00Z',
    sessionName: null,
    model: 'claude-opus-4-7',
    kind,
    epicId: null,
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    commits: [],
    ownerClientId: 'local-default',
    tags: [],
  };
}

// One epic per representative lane; one ordinary task per lane to prove the
// lane still renders its tasks. Titles are crafted so none is a substring of
// another (Playwright `hasText` does substring matching).
const EPIC_BACKLOG = makeCard('hide-epic-backlog', '0-backlog', 'Container epic alpha', 1, 'epic');
const TASK_BACKLOG = makeCard('hide-task-backlog', '0-backlog', 'Standalone backlog bravo', 2, 'task');
const EPIC_READY = makeCard('hide-epic-ready', '2-ready', 'Container epic charlie', 1, 'epic');
const TASK_READY = makeCard('hide-task-ready', '2-ready', 'Standalone ready delta', 2, 'task');
const EPIC_PROGRESS = makeCard('hide-epic-progress', '3-progress', 'Container epic echo', 1, 'epic');
const TASK_PROGRESS = makeCard('hide-task-progress', '3-progress', 'Standalone progress foxtrot', 2, 'task');
const EPIC_HUMAN = makeCard('hide-epic-human', '5-human-review', 'Container epic golf', 1, 'epic');
const TASK_HUMAN = makeCard('hide-task-human', '5-human-review', 'Standalone human hotel', 2, 'task');
const EPIC_ESCALATED = makeCard('hide-epic-escalated', '5e-escalated', 'Container epic india', 1, 'epic');
const TASK_ESCALATED = makeCard('hide-task-escalated', '5e-escalated', 'Standalone escalated juliet', 2, 'task');

const EPICS = [EPIC_BACKLOG, EPIC_READY, EPIC_PROGRESS, EPIC_HUMAN, EPIC_ESCALATED];
const TASKS = [TASK_BACKLOG, TASK_READY, TASK_PROGRESS, TASK_HUMAN, TASK_ESCALATED];

const GROUPED_PAYLOAD = {
  backlog: [EPIC_BACKLOG, TASK_BACKLOG],
  preparation: [],
  orchestratorPrep: [],
  ready: [EPIC_READY, TASK_READY],
  progress: [EPIC_PROGRESS, TASK_PROGRESS],
  failedPickup: [],
  codeNotComplete: [],
  review: [],
  autoReview: [],
  humanReview: [EPIC_HUMAN, TASK_HUMAN],
  escalated: [EPIC_ESCALATED, TASK_ESCALATED],
  completed: [],
  archive: [],
};

async function installRoutes(page: Page) {
  await page.route('**/api/**', (route) => {
    const url = route.request().url();
    if (url.endsWith('/api/tasks')) {
      route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
      return;
    }
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => undefined);
  });

  await page.route('**/api/v1/studio/board**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(GROUPED_PAYLOAD) }));

  await page.route('**/api/watch-paths**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH }]),
    }));

  await page.route('**/api/git/summary**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route(/\/api\/git\/hygiene(\?|$)/, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '{}' }));
  await page.route('**/api/environment**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } }),
    }));
  await page.route('**/api/agent-rules**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route('**/api/clients', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route('**/api/cli/usage**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: '2026-06-09T07:00:00Z', sessions: [] }) }));
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: '2026-06-09T07:00:00Z', ttlSeconds: 600, snapshots: [] }) }));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        projects: {
          [PROJECT]: { projectName: PROJECT, mode: 'manual', activeJobId: null, activeExecution: null, queuedJobIds: [] },
        },
      }),
    }));
  await page.route('**/api/tags', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
}

async function seedBoardTab(page: Page): Promise<void> {
  await page.addInitScript(() => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'board', projectName: '__all__' }],
      activeKey: 'board:__all__',
    }));
    // Ensure the board opens in lane (not group-by-epic) mode.
    try { localStorage.setItem('boardGroupByEpic', '0'); } catch { /* ignore */ }
  });
}

async function gotoBoard(page: Page): Promise<void> {
  await seedBoardTab(page);
  await installRoutes(page);
  await page.goto('/?includeFixtures=true');
  await page.waitForLoadState('domcontentloaded');
  await expect(page.locator('[data-testid="studio-board"], [data-testid="kanban-dashboard"]').first())
    .toBeVisible({ timeout: 15_000 });
  await expect(page.locator('[data-testid="task-card"]').first()).toBeVisible({ timeout: 15_000 });
}

function cardByTitle(page: Page, title: string) {
  return page.locator('[data-testid="task-card"]', { hasText: title });
}

test.describe('Board hides epics (tasks-only lanes)', () => {
  test('no epic renders as a card in any lane; tasks still render', async ({ page }) => {
    await gotoBoard(page);

    // Every ordinary task is on the board.
    for (const task of TASKS) {
      await expect(cardByTitle(page, task.title), `task card ${task.id}`).toHaveCount(1);
    }

    // No epic appears as a board card, in any lane.
    for (const epic of EPICS) {
      await expect(cardByTitle(page, epic.title), `epic card ${epic.id} must be hidden`).toHaveCount(0);
    }
  });

  test('epics remain reachable via the Group-by-epic view', async ({ page }) => {
    await gotoBoard(page);

    // Switch the board to the epic grouping (epic navigation surface).
    await page.getByTestId('studio-board-epic-toggle').click();

    const epicBoard = page.getByTestId('epic-group-board');
    await expect(epicBoard).toBeVisible({ timeout: 10_000 });

    // Each epic now surfaces as a group header (reachable, just not a lane card).
    for (const epic of EPICS) {
      const group = page.getByTestId(`epic-group-${epic.id}`);
      await expect(group, `epic group ${epic.id}`).toHaveCount(1);
      await expect(group).toContainText(epic.title);
    }
  });
});
