import { test, expect, Page } from '@playwright/test';
import path from 'node:path';

/**
 * Multi-aspect post-processing surface (slice 1).
 *
 * Verifies the kanban-side contract for the multi-aspect quality pass
 * on jobs in `4-auto-review`:
 *   - the Post Processing lane header stays quiet and does not render the
 *     old per-tick counter line;
 *   - an info button next to the lane title opens a drawer that explains
 *     what post-processing does;
 *   - jobs that picked up a `<namespace>:concerns` tag from the pipeline
 *     do NOT render a concern chip on the card (ASS-748: the lane already
 *     says the card is in auto-review, so concern/classifier markers are
 *     suppressed as lane-derivable noise).
 *
 * Fully fixture-driven via `page.route` intercepts so the spec doesn't
 * depend on a real running orchestrator.
 */

interface JobInfoStub {
  id: string;
  taskKey: string;
  title: string;
  state: string;
  order: number;
  agent: string;
  createdAt: string;
  watchPath: string;
  projectName: string;
  folderPath: string;
  lastActivity: string;
  sessionName: null;
  model: string | null;
  cliType: string | null;
  useOwnSession: null;
  lastUsage: null;
  execution: null;
  commit: null;
  ownerClientId: string;
  tokenSummary: null;
  tags: string[];
  reviewFailure?: {
    failureClass: string;
    reason: string;
    retryNumber: number;
    maximumRetries: number;
    retryAtUtc: string | null;
    exhausted: boolean;
  };
}

function jobStub(over: Partial<JobInfoStub>): JobInfoStub {
  const id = over.id ?? 'stub-job';
  return {
    id,
    taskKey: `stub::${id}`,
    title: over.title ?? id,
    state: over.state ?? '4-auto-review',
    order: over.order ?? 1,
    agent: 'claude',
    createdAt: '2026-05-05T08:00:00Z',
    watchPath: 'C:/stub',
    projectName: 'stub-project',
    folderPath: 'C:/stub/' + id,
    lastActivity: '2026-05-05T08:00:00Z',
    sessionName: null,
    model: 'claude-sonnet-4-6',
    cliType: 'claude',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    ownerClientId: 'local-default',
    tokenSummary: null,
    tags: over.tags ?? [],
    ...over
  };
}

interface AutoReviewStatus {
  lastTickAt: string | null;
  accept: number;
  reissue: number;
  escalate: number;
  aspectsRun: number;
  pending: number;
  currentJob: string | null;
  currentProject: string | null;
  activeJobs?: { project: string; jobId: string; step: string; startedAt: string }[];
}

async function installMocks(
  page: Page,
  jobs: JobInfoStub[],
  status: AutoReviewStatus
): Promise<void> {
  await page.route('**/api/**', async (route) => {
    const url = new URL(route.request().url());
    const p = url.pathname;
    if (p === '/api/auth/status') {
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null }),
      });
    }
    if (p === '/api/tasks/grouped' || p === '/api/tasks/grouped') {
      const body = {
        backlog: jobs.filter((j) => j.state === '0-backlog'),
        preparation: jobs.filter((j) => j.state === '1-preparation'),
        orchestratorPrep: [],
        ready: jobs.filter((j) => j.state === '2-ready'),
        progress: jobs.filter((j) => j.state === '3-progress'),
        failedPickup: [],
        autoReview: jobs.filter((j) => j.state === '4-auto-review'),
        humanReview: jobs.filter((j) => j.state === '5-human-review'),
        review: jobs.filter((j) => j.state === '4-auto-review'),
        completed: jobs.filter((j) => j.state === '6-completed'),
        archive: jobs.filter((j) => j.state === '7-archive')
      };
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
    }
    if (p === '/api/tasks/archive') {
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ items: [], total: 0, offset: 0, limit: 50 }),
      });
    }
    if (p === '/api/crash-recovery/pending') {
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ pending: [] }),
      });
    }
    if (p === '/api/tasks' || p === '/api/tasks/' || p === '/api/tasks' || p === '/api/tasks/') {
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(jobs) });
    }
    if (p === '/api/auto-review/status') {
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(status) });
    }
    if (p.startsWith('/api/concept-docs/')) {
      const topic = p.substring('/api/concept-docs/'.length);
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          topic,
          title: 'Post Processing',
          body: 'Post Processing runs a multi-aspect quality pass on every job that ends with `[[TASK_DONE]]`. Each aspect writes its own `aspect-*.md` into the job folder.\n\nWhen all aspects pass, the orchestrator promotes the job to review.'
        })
      });
    }
    if (p === '/api/tags') {
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([]) });
    }
    if (p.startsWith('/api/clients')) {
      const list = [{
        id: 'local-default', displayName: 'Local Default', emoji: '🤖', colour: '#64748b', kind: 'human',
        registeredAt: '2026-01-01T00:00:00Z', lastSeenAt: null, tokenBudgetMonthly: null, notes: null
      }];
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(list) });
    }
    if (p === '/api/watch-paths') {
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([
        { name: 'stub-project', path: 'C:/stub', rootPath: 'C:/stub' }
      ]) });
    }
    if (p === '/api/workspaces') {
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify([{
          id: 'default',
          displayName: 'Workspaces',
          sortOrder: 0,
          isDefault: true,
          color: null,
          createdAt: '2026-01-01T00:00:00Z',
          projects: [{
            id: 'stub-project',
            displayName: 'stub-project',
            shortCode: 'SP',
            workspaceId: 'default',
            color: null,
            cliDefault: null,
            modelDefault: null,
            sortOrder: 0,
            storageLocation: 'C:/stub',
            archived: false,
            createdAt: '2026-01-01T00:00:00Z'
          }]
        }])
      });
    }
    if (p === '/api/environment') {
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } })
      });
    }
    if (p === '/api/projects/settings') {
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({}) });
    }
    if (p === '/api/orchestrator/sessions') {
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ sessions: [] }),
      });
    }
    if (p === '/api/dev-tools/flags') {
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ updateStableEnabled: false, deleteE2EJobsEnabled: false })
      });
    }
    if (p.match(/^\/api\/cli\/[^/]+\/models$/)) {
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ models: [], defaultModel: null }) });
    }
    if (p === '/api/settings/cli/models') {
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ models: [] }) });
    }
    if (p === '/api/cli/quota') {
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: new Date().toISOString(), ttlSeconds: 600, snapshots: [] }) });
    }
    if (p === '/api/cli/usage') {
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: new Date().toISOString(), sections: [] }) });
    }
    if (p.startsWith('/api/runner')) {
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ projects: {} }) });
    }
    return route.fulfill({ status: 200, contentType: 'application/json', body: 'null' });
  });
}

async function dismissErrorDialogIfPresent(page: Page): Promise<void> {
  const overlay = page.getByTestId('error-dialog-overlay');
  if (await overlay.isVisible().catch(() => false)) {
    await page.keyboard.press('Escape');
    await overlay.waitFor({ state: 'hidden', timeout: 2_000 }).catch(() => { /* best-effort */ });
  }
}

async function restoreAllProjectsBoard(page: Page): Promise<void> {
  await page.addInitScript(() => {
    window.localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'board', projectName: '__all__' }],
      activeKey: 'board:__all__'
    }));
  });
}

test.describe('Auto-review multi-aspect surface', () => {
  test('lane header omits tick counters and info drawer opens', async ({ page }) => {
    const job = jobStub({ id: 'fixture-pass', title: 'All aspects pass', state: '4-auto-review', tags: [] });
    await installMocks(page, [job], {
      lastTickAt: new Date(Date.now() - 12_000).toISOString(),
      accept: 4,
      reissue: 1,
      escalate: 0,
      aspectsRun: 16,
      pending: 6,
      currentJob: 'fixture-pass',
      currentProject: 'stub-project'
    });
    await restoreAllProjectsBoard(page);

    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await dismissErrorDialogIfPresent(page);

    await expect(page.getByTestId('lane-4-auto-review')).toBeVisible({ timeout: 10_000 });
    await expect(page.getByTestId('auto-review-status')).toHaveCount(0);
    await expect(page.getByTestId('lane-4-auto-review')).not.toContainText('Last tick:');

    // Info button next to the lane title opens the centered lane-info
    // modal with the rendered concept doc fetched from /api/concept-docs/.
    // The body text comes from docs/app/help/lane-guides/lane-4-auto-review.md.
    const infoBtn = page.getByTestId('info-button-lane-4-auto-review');
    await expect(infoBtn).toBeVisible();
    await infoBtn.click();

    const modal = page.getByTestId('info-button-modal-lane-4-auto-review');
    await expect(modal).toBeVisible();
    await expect(modal).toContainText(/multi-aspect/i);
    await expect(modal).toContainText(/aspect-/);

    // Capture lane-header evidence (no tick line + open modal + info button).
    await page.setViewportSize({ width: 1400, height: 900 });
    await page.screenshot({ path: 'screenshots/auto-review/auto-review-lane-header.png', fullPage: false });

    // Close the modal.
    await page.getByTestId('info-button-modal-lane-4-auto-review-close').click();
    await expect(modal).toHaveCount(0);
  });

  // ASS-748: concern/classifier tags are lane-derivable noise. A card in
  // 4-auto-review must NOT repeat that it is in post-processing nor surface the
  // pipeline's `<namespace>:concerns` markers; the card carries only
  // non-lane-derivable information.
  test('job with quality:concerns tags renders no concern chip and no lane-mirroring status', async ({ page }) => {
    const job = jobStub({
      id: 'fixture-concerns',
      title: 'Auto-review flagged concerns',
      state: '4-auto-review',
      tags: ['quality:concerns', 'docs:concerns']
    });
    await installMocks(page, [job], {
      lastTickAt: new Date(Date.now() - 5_000).toISOString(),
      accept: 1,
      reissue: 0,
      escalate: 0,
      aspectsRun: 4,
      pending: 1,
      currentJob: null,
      currentProject: null
    });
    await restoreAllProjectsBoard(page);

    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await dismissErrorDialogIfPresent(page);

    const card = page.locator('[data-testid="task-card"]', { hasText: 'Auto-review flagged concerns' });
    await expect(card).toBeVisible({ timeout: 10_000 });

    // Concern chips are suppressed: the lane already says it is in post-processing.
    await expect(card.locator('[data-testid="task-card-concern-chip"]')).toHaveCount(0);
    await expect(card.getByText('quality:concerns')).toHaveCount(0);
    await expect(card.getByText('docs:concerns')).toHaveCount(0);

    // The only lane-level state repeated on the card is useful activity:
    // this idle card shows its elapsed queue wait, not a generic review chip.
    await expect(card.getByTestId('task-card-post-processing-activity')).toContainText('waiting');

    // Capture suppression evidence on the card.
    await page.setViewportSize({ width: 1400, height: 900 });
    await card.screenshot({ path: 'screenshots/auto-review/auto-review-concerns-card.png' });
  });

  test('infrastructure failure shows its class and retry counter in Post Processing', async ({ page }) => {
    const job = jobStub({
      id: 'fixture-infrastructure',
      title: 'Fetch delivery branch',
      state: '4-auto-review',
      reviewFailure: {
        failureClass: 'infrastructure',
        reason: "Integration branch 'develop' could not be fetched from origin: git operation timed out after 30 seconds",
        retryNumber: 2,
        maximumRetries: 3,
        retryAtUtc: '2026-09-07T13:10:00Z',
        exhausted: false,
      },
    });
    await installMocks(page, [job], {
      lastTickAt: null,
      accept: 0,
      reissue: 0,
      escalate: 0,
      aspectsRun: 0,
      pending: 1,
      currentJob: null,
      currentProject: null,
    });
    await restoreAllProjectsBoard(page);

    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await dismissErrorDialogIfPresent(page);

    const card = page.locator('[data-testid="task-card"]', { hasText: 'Fetch delivery branch' });
    const failure = card.getByTestId('task-card-review-failure');
    await expect(card).toBeVisible({ timeout: 10_000 });
    await expect(failure).toContainText('infrastructure');
    await expect(failure).toContainText('retry 2/3');
    await expect(failure).toHaveAttribute('data-failure-class', 'infrastructure');

    if (process.env.JOB_RESULTS_DIR) {
      await dismissErrorDialogIfPresent(page);
      await expect(page.getByTestId('error-dialog-overlay')).toBeHidden();
      await card.scrollIntoViewIfNeeded();
      await card.screenshot({
        path: path.join(process.env.JOB_RESULTS_DIR, 'infrastructure-retry-card--mocked.png'),
      });
    }
  });
});
