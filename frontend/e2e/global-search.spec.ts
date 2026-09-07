import { test, expect } from './fixtures/dev-backend';

/**
 * How long the stubbed repository fan-out is held open. Long enough that the
 * assertions about "tasks are readable while repositories are still running"
 * are about the palette's contract, not about a race.
 */
const REPOSITORY_DELAY_MS = 2_500;

function frame(event: string, data: unknown): string {
  return `event: ${event}\ndata: ${JSON.stringify(data)}\n\n`;
}

const FILE_MATCH = {
  domain: 'files', projectName: 'Agent Studio', projectColor: '#569cd6',
  title: 'README.md', subtitle: 'README.md', path: 'README.md', isWiki: false,
};

const STREAM_BODY = [
  frame('tasks', { items: [], durationMs: 6, error: null }),
  frame('progress', { completed: 0, total: 2 }),
  frame('repository', {
    projectName: 'Agent Studio', completed: 1, total: 2,
    commits: [], files: [FILE_MATCH], durationMs: 1_400, fromCache: false, failedDomains: [],
  }),
  frame('repository', {
    projectName: 'Runner', completed: 2, total: 2,
    commits: [], files: [], durationMs: 900, fromCache: true, failedDomains: [],
  }),
  frame('done', { durationMs: 2_400, tasksMs: 6, repositoriesMs: 2_394, repositories: 2 }),
].join('');

const BOARD_TASK = {
  id: 'agt-2723-proof', taskKey: 'demo::agt-2723-proof', key: 'AGT-2723',
  title: 'README streamed search proof', state: '2-ready', projectName: 'Agent Studio',
  watchPath: '/tmp/demo', folderPath: '/tmp/demo/agt-2723-proof', order: 1, agent: 'claude',
  commits: [], tags: [], sessionChain: [], integrationRecords: [], relatedWikiPages: [],
  postProcessingChecks: [], references: {},
};

test('palette shows task results and repository progress before repository results land', async ({ page }) => {
  // The board snapshot is what the task group matches against with no round
  // trip at all, so one card is enough to prove the ordering.
  await page.route('**/api/tasks/grouped**', route => route.fulfill({
    contentType: 'application/json',
    // Every lane, not just the populated one: board consumers iterate the
    // whole grouped payload and a missing lane throws.
    body: JSON.stringify({
      backlog: [], preparation: [], orchestratorPrep: [], ready: [BOARD_TASK], progress: [],
      failedPickup: [], codeNotComplete: [], autoReview: [], humanReview: [], escalated: [],
      review: [], completed: [], archive: [],
    }),
  }));

  // Hold the repository fan-out open so the palette has to render the fast
  // domain on its own. Before AGT-2723 there was nothing to render: one
  // spinner covered the whole search and no result appeared until the slowest
  // checkout answered.
  await page.route('**/api/search/stream?**', async route => {
    await new Promise(resolve => setTimeout(resolve, REPOSITORY_DELAY_MS));
    await route.fulfill({ contentType: 'text/event-stream', body: STREAM_BODY });
  });

  await page.addInitScript(() => localStorage.setItem('atp.studio.theme', 'light'));
  await page.goto('/');
  await page.getByTestId('studio-global-search-trigger').dispatchEvent('click');
  const input = page.getByTestId('global-search-input');
  await expect(input).toBeFocused();
  await input.fill('README streamed search proof');

  // Task matches come from the board snapshot, so they are on screen while the
  // repositories are still being searched, and the palette says which domain
  // is still working instead of showing one undifferentiated spinner.
  await expect(page.getByTestId('global-search-group-tasks')).toContainText(BOARD_TASK.title);
  await expect(page.getByTestId('global-search-status-files')).toHaveAttribute('data-status', 'searching');
  await expect(page.getByTestId('global-search-group-files')).not.toContainText('README.md');
  await expect(page.getByTestId('global-search-elapsed')).toBeVisible();

  // The in-flight frame is the feature: readable task results next to a domain
  // row that names how far the repository fan-out has got.
  const progressShot = process.env.GLOBAL_SEARCH_PROGRESS_SCREENSHOT;
  if (progressShot) await page.screenshot({ path: progressShot, fullPage: true });

  // The stubbed fan-out lands: file results append and the domain reports done.
  await expect(page.getByTestId('global-search-group-files')).toContainText('README.md', { timeout: 10_000 });
  await expect(page.getByTestId('global-search-status-files')).toHaveAttribute('data-status', 'done');
  await expect(page.getByTestId('global-search-group-tasks')).toContainText(BOARD_TASK.title);

  await page.keyboard.press('ArrowDown');
  await expect(page.locator('[role="option"][aria-selected="true"]')).toHaveCount(1);

  // A backendless worktree serve can report runner-status startup noise after
  // the palette has opened. Keep that unrelated global dialog out of the
  // feature evidence frame.
  await page.getByTestId('error-dialog-close').click({ timeout: 2_000 }).catch(() => {});

  const screenshotPath = process.env.GLOBAL_SEARCH_SCREENSHOT;
  if (screenshotPath) await page.screenshot({ path: screenshotPath, fullPage: true });

  await page.keyboard.press('Escape');
  await page.evaluate(() => localStorage.setItem('atp.studio.theme', 'dark'));
  await page.reload();
  await page.getByTestId('studio-global-search-trigger').dispatchEvent('click');
  await expect(page.getByTestId('global-search-input')).toBeVisible();
});
