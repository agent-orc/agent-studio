import { test, expect } from './fixtures/dev-backend';
import { join } from 'node:path';
import { auditModal } from './helpers/audit-modal';

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

for (const width of [1280, 390]) {
  test(`Ctrl+K search closes once and restores focus at ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: 800 });
    await page.route('**/api/**', route => route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
    await page.route('**/api/v1/studio/auth/status', route => route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null }),
    }));
    await page.route('**/api/v1/studio/board**', route => route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify({
        backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
        failedPickup: [], codeNotComplete: [], autoReview: [], humanReview: [],
        escalated: [], review: [], completed: [], archive: [],
      }),
    }));
    await page.route('**/api/tasks/archive**', route => route.fulfill({
      status: 200, contentType: 'application/json', body: JSON.stringify({ items: [], total: 0 }),
    }));
    await page.route('**/api/v1/studio/runner/status**', route => route.fulfill({
      status: 200, contentType: 'application/json', body: JSON.stringify({ projects: {} }),
    }));
    await page.goto('/');
    const opener = page.locator('button:visible').first();
    await opener.focus();
    await page.keyboard.press('Control+k');
    const input = page.getByTestId('global-search-input');
    await expect(input).toBeFocused();
    await auditModal(page, '[data-testid="global-search-backdrop"] [role="dialog"]', `global-search-${width}`);
    if (process.env.JOB_RESULTS_DIR) {
      await page.screenshot({ path: join(process.env.JOB_RESULTS_DIR, `global-search-keyboard-${width}.png`) });
    }
    await page.keyboard.press('Tab');
    await expect.poll(() => page.evaluate(() =>
      !!document.querySelector('[data-testid="global-search-backdrop"] [role="dialog"]')?.contains(document.activeElement))).toBe(true);
    await page.keyboard.press('Escape');
    await expect(input).toHaveCount(0);
    await expect(opener).toBeFocused();
    await page.waitForTimeout(400);
    await expect(input).toHaveCount(0);
    await expect(opener).toBeFocused();
  });
}

test('palette shows task results and repository progress before repository results land', async ({ page }) => {
  // The board snapshot is what the task group matches against with no round
  // trip at all, so one card is enough to prove the ordering.
  await page.route('**/api/v1/studio/board**', route => route.fulfill({
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

const DOSSIER_MATCH = {
  domain: 'dossiers', projectName: 'Agent Studio', projectColor: '#569cd6',
  title: 'Task-Detail: Verbrauchsblock in Pipeline-Breite',
  subtitle: 'Usage metrics span the task-detail pipeline width.',
  dossierKey: 'AGT-W48', referenceKey: 'AGT-W48', workbenchId: 'task-detail-usage-panel',
  projectId: 'PROJ-002', lane: 'active', phase: 'testing', updatedAt: '2026-09-11T10:00:00Z',
};

const WIKI_MATCH = {
  domain: 'wiki', projectName: 'Agent Studio', projectColor: '#569cd6',
  title: 'Task detail usage notes', subtitle: 'AGT-W48 acceptance evidence',
  path: 'operations/task-detail-usage-panel/notes.md', isWiki: true,
};

const DOSSIER_STREAM_BODY = [
  frame('tasks', { items: [], durationMs: 4, error: null }),
  frame('dossiers', { items: [DOSSIER_MATCH], durationMs: 5, error: null }),
  frame('wiki', { items: [WIKI_MATCH], durationMs: 5, error: null }),
  frame('progress', { completed: 0, total: 0 }),
  frame('done', { durationMs: 9, tasksMs: 4, repositoriesMs: 0, repositories: 0 }),
].join('');

test('palette groups Dossier and Wiki results and keyboard navigation opens the exact Dossier', async ({ page, devBackend }) => {
  void devBackend;
  await page.route('**/api/v1/studio/auth/status', route => route.fulfill({
    contentType: 'application/json',
    body: JSON.stringify({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null }),
  }));
  await page.route('**/api/search/stream?**', route => route.fulfill({
    contentType: 'text/event-stream', body: DOSSIER_STREAM_BODY,
  }));
  await page.route('**/api/projects/Agent%20Studio/workbenches/task-detail-usage-panel', route => route.fulfill({
    contentType: 'application/json',
    body: JSON.stringify({
      workbench: {
        id: 'task-detail-usage-panel', key: 'AGT-W48', title: DOSSIER_MATCH.title,
        summary: DOSSIER_MATCH.subtitle, status: 'active', phase: 'testing',
        updatedAtUtc: '2026-09-11T10:00:00Z', entryPath: 'docs/operations/task-detail-usage-panel/index.html',
        valid: true, error: null, sourceTaskKeys: [], relatedTaskKeys: [],
      },
      html: `<!doctype html><html><body><h1>${DOSSIER_MATCH.title}</h1></body></html>`,
      branch: 'main', revision: 'a'.repeat(40), workingTreeModified: false, fingerprint: 'b'.repeat(64),
    }),
  }));

  await page.addInitScript(() => localStorage.setItem('atp.studio.theme', 'light'));
  await page.goto('/');
  await page.getByTestId('error-dialog-close').click({ timeout: 5_000 }).catch(() => {});
  await page.getByTestId('studio-global-search-trigger').dispatchEvent('click');
  const input = page.getByTestId('global-search-input');
  await expect(input).toBeFocused();
  await input.fill('AGT-W48');

  const dossierGroup = page.getByTestId('global-search-group-dossiers');
  await expect(dossierGroup).toContainText(DOSSIER_MATCH.title);
  await expect(dossierGroup).toContainText('AGT-W48');
  await expect(dossierGroup).toContainText('testing');
  await expect(page.getByTestId('global-search-group-wiki')).toContainText('Task detail usage notes');
  await expect(page.getByTestId('global-search-domain-dossiers')).toHaveText('Dossiers');
  await expect(page.getByTestId('global-search-domain-wiki')).toHaveText('Wiki');
  const domainsShot = process.env.GLOBAL_SEARCH_DOMAINS_SCREENSHOT;
  if (domainsShot) await page.screenshot({ path: domainsShot, fullPage: true });

  // Exact Dossier key ranks first. Exercise both arrow directions and Enter
  // so the new domains share the task-row keyboard contract.
  await expect(page.locator('[role="option"][aria-selected="true"]')).toContainText(DOSSIER_MATCH.title);
  await page.keyboard.press('ArrowDown');
  await expect(page.locator('[role="option"][aria-selected="true"]')).toContainText('Task detail usage notes');
  await page.keyboard.press('ArrowUp');
  await page.keyboard.press('Enter');

  await expect(page).toHaveURL(/#\/projects\/PROJ-002\/workbenches\/task-detail-usage-panel(?:&|$)/);
  await expect(page.frameLocator('[data-testid="workbench-viewer-frame"]')
    .getByRole('heading', { name: DOSSIER_MATCH.title })).toBeVisible({ timeout: 30_000 });
});

// AGT-3591: a task result's lane badge is the lane's one name and one tone
// from lane-presentation, never the raw `5-human-review` slug.
for (const theme of ['dark', 'light'] as const) {
  test(`task result lane badge shows the lane name and tone (${theme})`, async ({ page }) => {
    const reviewTask = { ...BOARD_TASK, id: 'agt-3591-proof', taskKey: 'demo::agt-3591-proof', key: 'AGT-3591',
      title: 'Lane badge proof', state: '5-human-review', folderPath: '/tmp/demo/agt-3591-proof' };
    await page.route('**/api/v1/studio/board**', route => route.fulfill({
      contentType: 'application/json',
      body: JSON.stringify({
        backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
        failedPickup: [], codeNotComplete: [], autoReview: [], humanReview: [reviewTask], escalated: [],
        review: [], completed: [], archive: [],
      }),
    }));
    await page.route('**/api/search/stream?**', route => route.fulfill({
      contentType: 'text/event-stream',
      body: [frame('tasks', { items: [], durationMs: 1, error: null }), frame('progress', { completed: 0, total: 0 }),
        frame('done', { durationMs: 1, tasksMs: 1, repositoriesMs: 0, repositories: 0 })].join(''),
    }));
    await page.addInitScript(t => localStorage.setItem('atp.studio.theme', t), theme);
    await page.goto('/');
    await page.getByTestId('studio-global-search-trigger').dispatchEvent('click');
    await page.getByTestId('global-search-input').fill('Lane badge proof');

    const badge = page.getByTestId('global-search-group-tasks').getByTestId('global-search-lane-badge');
    await expect(badge).toHaveText('Human review');
    await expect(badge).toHaveAttribute('data-lane-tone', 'human-review');
    const colours = await badge.evaluate(el => {
      const probe = document.createElement('span');
      probe.style.color = 'var(--studio-lane-human-review-fg)';
      document.body.appendChild(probe);
      const expected = getComputedStyle(probe).color;
      probe.remove();
      return { actual: getComputedStyle(el).color, expected };
    });
    expect(colours.actual).toBe(colours.expected);
    if (process.env.JOB_RESULTS_DIR) {
      await page.screenshot({ path: join(process.env.JOB_RESULTS_DIR, `global-search-lane-badge-${theme}.png`) });
    }
  });
}
