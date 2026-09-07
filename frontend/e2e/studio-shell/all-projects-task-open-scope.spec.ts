import { test, expect, type Page } from '@playwright/test';

/**
 * AGT-2692: opening a task from the cross-project "All projects" board used
 * to narrow `BoardFiltersService.activeProjects` to that task's single
 * project as a side effect of the studio-tab-to-scope mirror in `app.ts`.
 * The task's *data* fetch was always per-request and unaffected, but the
 * workspace-wide active-project scope — the same signal the lane-peer pager
 * and the board itself read — got silently narrowed the moment the task tab
 * became active.
 *
 * This spec seeds two projects on the All-projects board, opens a task that
 * belongs to only one of them, and asserts the task's own Prev/Next pager
 * still counts peers across BOTH projects (the workspace-wide scope the user
 * was in when they clicked), then closes the tab and confirms the board is
 * still showing both projects, not narrowed down to one.
 */

const ALL_BOARD_KEY = 'board:__all__';

function job(over: {
  id: string;
  taskKey: string;
  title: string;
  projectName: string;
  watchPath: string;
}) {
  return {
    id: over.id,
    taskKey: over.taskKey,
    key: over.taskKey.toUpperCase().replace(/[^A-Z0-9]/g, '').slice(0, 10),
    title: over.title,
    state: '2-ready',
    order: 1,
    agent: 'codex',
    createdAt: '2026-01-01T00:00:00Z',
    watchPath: over.watchPath,
    projectName: over.projectName,
    folderPath: `${over.watchPath}/.orchestrator/jobs/${over.id}`,
    lastActivity: '2026-01-01T00:00:00Z',
    sessionName: null,
    model: null,
    cliType: 'codex',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
  };
}

const TASK_A1 = job({ id: 'task-a1', taskKey: 'C:/watch-a::task-a1', title: 'Project A — first task', projectName: 'Project A', watchPath: 'C:/watch-a' });
const TASK_A2 = job({ id: 'task-a2', taskKey: 'C:/watch-a::task-a2', title: 'Project A — second task', projectName: 'Project A', watchPath: 'C:/watch-a' });
const TASK_B1 = job({ id: 'task-b1', taskKey: 'C:/watch-b::task-b1', title: 'Project B — only task', projectName: 'Project B', watchPath: 'C:/watch-b' });

const GROUPED = {
  backlog: [], preparation: [], orchestratorPrep: [],
  ready: [TASK_A1, TASK_A2, TASK_B1],
  progress: [], failedPickup: [], review: [], autoReview: [],
  humanReview: [], completed: [], archive: [],
};

async function bootStudio(page: Page): Promise<void> {
  page.on('console', msg => console.log('BROWSER', msg.type(), msg.text()));
  page.on('pageerror', err => console.log('PAGEERROR', err.message));
  await page.route('**/api/**', route => {
    const url = route.request().url();
    const json = (body: unknown) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
    if (url.includes('/api/auth/status')) {
      return json({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null });
    }
    if (url.includes('/api/tasks/grouped')) return json(GROUPED);
    if (url.includes('/api/runner/status')) return json({ projects: {} });
    if (url.includes('/api/tasks/task-a1')) {
      return json({
        info: TASK_A1,
        promptMarkdown: '', promptHistory: [], titleHistory: [],
        statusMarkdown: null, contextUsage: null, log: [],
        summaryState: null, reviewEvidence: [],
      });
    }
    if (/\/api\/tasks(\?|$)/.test(url)) return json([TASK_A1, TASK_A2, TASK_B1]);
    if (url.includes('/api/watch-paths')) {
      return json([
        { name: 'Project A', path: 'C:/watch-a', rootPath: 'C:/watch-a' },
        { name: 'Project B', path: 'C:/watch-b', rootPath: 'C:/watch-b' },
      ]);
    }
    return route.continue();
  });

  await page.goto('/');
  await expect(page.getByTestId('app-root')).toBeVisible({ timeout: 15_000 });
}

test.describe('studio-shell · opening a task from the All-projects board keeps the workspace-wide scope', () => {
  test.setTimeout(90_000);

  test('task pager stays cross-project and the board is unnarrowed on return', async ({ page }) => {
    await bootStudio(page);
    // Matches JobArtifactReporter's spec-name derivation (basename minus
    // extension) so these screenshots get harvested into JOB_RESULTS_DIR.
    const shotDir = 'test-results/all-projects-task-open-scope';

    // Both projects' cards are visible on the All-projects board.
    const aCards = page.locator('[data-testid="task-card"][data-project="Project A"]');
    const bCards = page.locator('[data-testid="task-card"][data-project="Project B"]');
    await expect(aCards).toHaveCount(2);
    await expect(bCards).toHaveCount(1);
    await page.screenshot({ path: `${shotDir}/01-before-all-projects-board.png`, fullPage: true });

    // Open the Project A task from the All-projects board.
    await aCards.filter({ hasText: 'Project A — first task' }).click();
    await page.waitForTimeout(1000);
    await page.screenshot({ path: `${shotDir}/debug-immediately-after-click.png`, fullPage: true });
    await expect(page.getByTestId('studio-task')).toBeVisible({ timeout: 30_000 });
    await page.screenshot({ path: `${shotDir}/debug-task-open.png`, fullPage: true });

    // The task's own Prev/Next pager must count peers across BOTH projects
    // (3 ready tasks total) — the workspace-wide scope the user was in when
    // they clicked — not just the 2 peers that belong to the task's own
    // project. A regression here means opening the task silently narrowed
    // the active-project scope before the pager snapshot was captured.
    await expect(page.getByTestId('studio-task-pager-position')).toHaveText('1 / 3');
    await page.screenshot({ path: `${shotDir}/02-after-task-open-pager-cross-project.png`, fullPage: true });

    // Close the task tab — the recovery path returns to the All-projects
    // board tab via the existing MRU tab-close behavior.
    await page.locator(`.studio-tab[data-tab-key="task:${TASK_A1.taskKey}"] .studio-tab__close`).click();
    await expect(page.locator(`.studio-tab[data-tab-key="${ALL_BOARD_KEY}"]`)).toHaveClass(/studio-tab--active/);

    // Both projects are still visible — the board was never narrowed down
    // to Project A behind the scenes.
    await expect(aCards).toHaveCount(2);
    await expect(bCards).toHaveCount(1);
    await page.screenshot({ path: `${shotDir}/03-after-close-all-projects-board.png`, fullPage: true });
  });
});
