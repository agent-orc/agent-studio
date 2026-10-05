import { test, expect, Page } from '@playwright/test';

/**
 * Per-project token total badge in the header chip strip.
 *
 * The board's `app-project-tabs` chip strip is the surface where the user
 * sees every watched project at a glance. The user requested visible AI
 * spend at the project-board level so they don't have to open a single
 * project's deep view to know whether tokens have been burned. The chip
 * aggregates `JobInfo.tokenSummary` totals over every job in the project
 * and renders a compact badge after the project name when the total is
 * greater than zero.
 *
 * Contract checked here:
 *  - chip with no tokens: no badge.
 *  - chip with tokens: badge visible with the compact label and a tooltip
 *    that names the input/output split, the task count, and the models
 *    used (covering the "model change in meta-tasks" surface).
 *  - the chip badge stays consistent with the per-card bubble shorthand
 *    (kilotokens / megatokens), so the two surfaces don't disagree.
 */

const SHOTS = 'screenshots/project-tab-tokens';

interface JobInfoStub {
  id: string;
  jobKey: string;
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
  tokenSummary: null | {
    calls: number;
    inputTokens: number;
    outputTokens: number;
    cacheReadTokens: number;
    cacheCreationTokens: number;
    totalTokens: number;
    lastModel: string | null;
    lastUpdate: string | null;
    entries: Array<{
      ts: string;
      model: string | null;
      inputTokens: number;
      outputTokens: number;
      cacheReadTokens: number;
      cacheCreationTokens: number;
    }>;
  };
}

function jobStub(over: Partial<JobInfoStub>): JobInfoStub {
  const id = over.id ?? 'stub-job';
  const projectName = over.projectName ?? 'project-a';
  return {
    id,
    jobKey: `${projectName}::${id}`,
    title: over.title ?? id,
    state: over.state ?? '2-ready',
    order: over.order ?? 1,
    agent: 'copilot',
    createdAt: '2026-05-05T08:00:00Z',
    watchPath: 'C:/' + projectName,
    projectName,
    folderPath: `C:/${projectName}/${id}`,
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
    ...over
  };
}

async function stubBoard(page: Page, jobs: JobInfoStub[], projectNames: string[]): Promise<void> {
  await page.route('**/api/**', async (route) => {
    const url = new URL(route.request().url());
    const p = url.pathname;
    if (p === '/api/v1/studio/board') {
      const body = {
        preparation: jobs.filter((j) => j.state === '1-preparation'),
        ready: jobs.filter((j) => j.state === '2-ready'),
        progress: jobs.filter((j) => j.state === '3-progress'),
        review: jobs.filter((j) => j.state === '4-review'),
        completed: jobs.filter((j) => j.state === '5-completed'),
        archive: jobs.filter((j) => j.state === '6-archive')
      };
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
    }
    if (p === '/api/tasks' || p === '/api/tasks/') {
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(jobs) });
    }
    if (p.startsWith('/api/clients')) {
      const list = [{
        id: 'local-default', displayName: 'Local Default', emoji: '🤖', colour: '#64748b', kind: 'human',
        registeredAt: '2026-01-01T00:00:00Z', lastSeenAt: null, tokenBudgetMonthly: null, notes: null
      }];
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(list) });
    }
    if (p === '/api/watch-paths') {
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(projectNames.map((n) => ({ name: n, path: 'C:/' + n, rootPath: 'C:/' + n })))
      });
    }
    if (p === '/api/cli/quota') {
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: new Date().toISOString(), ttlSeconds: 600, snapshots: [] }) });
    }
    if (p === '/api/cli/usage') {
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: new Date().toISOString(), sections: [] }) });
    }
    if (p.startsWith('/api/runner') || p === '/api/v1/studio/runner/status') {
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ projects: {} }) });
    }
    return route.fulfill({ status: 200, contentType: 'application/json', body: 'null' });
  });
}

test.describe('Per-project token total badge', () => {
  test('project chip renders no badge when no jobs have token activity', async ({ page }) => {
    const quiet = jobStub({ id: 'q1', projectName: 'quiet-project', tokenSummary: null });
    await stubBoard(page, [quiet], ['quiet-project']);

    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');

    const chip = page.getByTestId('project-filter-quiet-project');
    await expect(chip).toBeVisible();
    await expect(chip.locator('[data-testid="project-tokens-quiet-project"]')).toHaveCount(0);
  });

  test('project chip renders aggregated badge across multiple jobs with token data', async ({ page }) => {
    // Two jobs, two distinct models so the tooltip exercises the
    // model-change history surface (meta-task case).
    const jobA = jobStub({
      id: 'a-job',
      projectName: 'noisy-project',
      title: 'Task A',
      tokenSummary: {
        calls: 2,
        inputTokens: 80_000,
        outputTokens: 12_000,
        cacheReadTokens: 50_000,
        cacheCreationTokens: 4_000,
        totalTokens: 146_000,
        lastModel: 'claude-sonnet-4-6',
        lastUpdate: '2026-05-05T08:30:00Z',
        entries: [
          { ts: '2026-05-05T08:00:00Z', model: 'claude-haiku-4-5', inputTokens: 30_000, outputTokens: 4_000, cacheReadTokens: 20_000, cacheCreationTokens: 2_000 },
          { ts: '2026-05-05T08:30:00Z', model: 'claude-sonnet-4-6', inputTokens: 50_000, outputTokens: 8_000, cacheReadTokens: 30_000, cacheCreationTokens: 2_000 }
        ]
      }
    });
    const jobB = jobStub({
      id: 'b-job',
      projectName: 'noisy-project',
      title: 'Task B',
      order: 2,
      tokenSummary: {
        calls: 1,
        inputTokens: 40_000,
        outputTokens: 6_000,
        cacheReadTokens: 0,
        cacheCreationTokens: 0,
        totalTokens: 46_000,
        lastModel: 'claude-opus-4-7',
        lastUpdate: '2026-05-05T09:00:00Z',
        entries: [
          { ts: '2026-05-05T09:00:00Z', model: 'claude-opus-4-7', inputTokens: 40_000, outputTokens: 6_000, cacheReadTokens: 0, cacheCreationTokens: 0 }
        ]
      }
    });
    await stubBoard(page, [jobA, jobB], ['noisy-project']);

    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');

    const chip = page.getByTestId('project-filter-noisy-project');
    await expect(chip).toBeVisible();
    const badge = chip.locator('[data-testid="project-tokens-noisy-project"]');
    await expect(badge).toBeVisible();
    // Total = 146k + 46k = 192k -> "192k" (>=10k uses no decimal).
    await expect(badge).toContainText('192k');

    // Tooltip lists input/output split, task count, and the models used.
    // The most-recently-used model lands first (claude-opus-4-7 at 09:00 > sonnet at 08:30 > haiku at 08:00).
    const tooltip = await badge.getAttribute('title');
    expect(tooltip).toBeTruthy();
    expect(tooltip!).toContain('120k input');
    expect(tooltip!).toContain('18k output');
    expect(tooltip!).toContain('2 tasks with AI activity');
    expect(tooltip!).toContain('claude-opus-4-7');
    expect(tooltip!).toContain('claude-sonnet-4-6');
    expect(tooltip!).toContain('claude-haiku-4-5');
    // Most-recent model first: opus appears before sonnet appears before haiku.
    const opusIdx = tooltip!.indexOf('claude-opus-4-7');
    const sonnetIdx = tooltip!.indexOf('claude-sonnet-4-6');
    const haikuIdx = tooltip!.indexOf('claude-haiku-4-5');
    expect(opusIdx).toBeGreaterThan(0);
    expect(sonnetIdx).toBeGreaterThan(opusIdx);
    expect(haikuIdx).toBeGreaterThan(sonnetIdx);

    // data attribute mirrors the numeric total for screen-reader / regression assertions.
    await expect(badge).toHaveAttribute('data-token-total', '192000');

    const chipBox = await chip.boundingBox();
    if (chipBox) {
      await page.screenshot({
        path: `${SHOTS}/project-chip-with-token-badge.png`,
        clip: {
          x: Math.max(0, chipBox.x - 8),
          y: Math.max(0, chipBox.y - 8),
          width: Math.min(page.viewportSize()!.width - chipBox.x + 8, chipBox.width + 200),
          height: chipBox.height + 16
        }
      });
    }
  });

  test('two projects on the same board each show their own aggregated total', async ({ page }) => {
    const aJob = jobStub({
      id: 'a1',
      projectName: 'project-alpha',
      tokenSummary: {
        calls: 1,
        inputTokens: 5_000,
        outputTokens: 1_000,
        cacheReadTokens: 0,
        cacheCreationTokens: 0,
        totalTokens: 6_000,
        lastModel: 'claude-haiku-4-5',
        lastUpdate: '2026-05-05T08:00:00Z',
        entries: [
          { ts: '2026-05-05T08:00:00Z', model: 'claude-haiku-4-5', inputTokens: 5_000, outputTokens: 1_000, cacheReadTokens: 0, cacheCreationTokens: 0 }
        ]
      }
    });
    const bJob = jobStub({
      id: 'b1',
      projectName: 'project-beta',
      order: 2,
      tokenSummary: {
        calls: 1,
        inputTokens: 1_500_000,
        outputTokens: 100_000,
        cacheReadTokens: 800_000,
        cacheCreationTokens: 50_000,
        totalTokens: 2_450_000,
        lastModel: 'claude-opus-4-7',
        lastUpdate: '2026-05-05T08:00:00Z',
        entries: [
          { ts: '2026-05-05T08:00:00Z', model: 'claude-opus-4-7', inputTokens: 1_500_000, outputTokens: 100_000, cacheReadTokens: 800_000, cacheCreationTokens: 50_000 }
        ]
      }
    });
    await stubBoard(page, [aJob, bJob], ['project-alpha', 'project-beta']);

    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');

    const alphaBadge = page.locator('[data-testid="project-tokens-project-alpha"]');
    const betaBadge = page.locator('[data-testid="project-tokens-project-beta"]');
    await expect(alphaBadge).toBeVisible();
    await expect(betaBadge).toBeVisible();
    // 6_000 -> "6.0k" (under 10k uses one decimal).
    await expect(alphaBadge).toContainText('6.0k');
    // 2_450_000 -> "2.5M" (under 10M uses one decimal).
    await expect(betaBadge).toContainText('2.5M');
  });
});
