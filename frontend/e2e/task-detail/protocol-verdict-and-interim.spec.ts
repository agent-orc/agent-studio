import { Page } from '@playwright/test';
import { test, expect } from '../fixtures/dev-backend';
import { api, BACKEND } from '../helpers/api';
import { createJob, listJobs } from '../helpers/jobs';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

interface WatchPathEntry { path: string; name: string }

function buildCompletedJobDetail(jobId: string, watchPath: string, statusMarkdown: string | null) {
  return {
    info: {
      id: jobId,
      taskKey: `${watchPath}::${jobId}`,
      jobKey: `${watchPath}::${jobId}`,
      title: 'Verdict duration spec fixture',
      state: '5-human-review',
      agent: 'claude',
      cliType: 'claude',
      model: 'claude-opus-4-7',
      watchPath,
      projectName: 'fixture',
      folderPath: `${watchPath}/.orchestrator/jobs/5-human-review/${jobId}`,
      sessionName: '00000000-0000-0000-0000-000000000000',
      lastUsage: null,
      execution: null,
      orchestratorVerdict: null,
      order: 1,
    },
    promptMarkdown: 'Pretend prompt.',
    statusMarkdown,
    log: [],
    promptHistory: [],
    summaryState: { status: 'ready', startedAt: null, finishedAt: null, errorMessage: null },
  };
}

async function installCompletedJobMocks(
  page: Page,
  target: { id: string; watchPath: string },
  statusMarkdown: string,
  detailOverride?: object,
): Promise<void> {
  const detailBody = JSON.stringify(detailOverride ?? buildCompletedJobDetail(target.id, target.watchPath, statusMarkdown));

  await page.route(`**/api/tasks/${encodeURIComponent(target.id)}?**`, async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: detailBody });
  });
  await page.route(`**/api/tasks/${encodeURIComponent(target.id)}/output?**`, async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
  });
  await page.route(`**/api/tasks/${encodeURIComponent(target.id)}/runs?**`, async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ runs: [] }) });
  });
  await page.route(`**/api/tasks/${encodeURIComponent(target.id)}/session-events?**`, async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ events: [], sessionChain: [] }),
    });
  });
  await page.route(`**/api/tasks/${encodeURIComponent(target.id)}/claude-session?**`, async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(null) });
  });
}

/**
 * Verifies the two protocol-pane additions for the
 * `protokollsummery-verbesserung` task:
 *
 *   1. One authoritative outcome banner renders at the very top of the
 *      protocol pane body. The status is derived deterministically from all
 *      current run signals and is reused by the Result and Pipeline surfaces.
 *
 *   2. The interim-status backend endpoint exists and surfaces the precondition
 *      failure (missing cli-output.log) verbatim - no silent 500.
 *
 * The full Haiku interim summary path is `@billable` and exercised by
 * `claude-hello-world.spec.ts` indirectly (same one-shot pipeline). Here we
 * lock the cheap branches so the UX wiring does not silently regress.
 */
test.describe('Protocol pane - verdict chip + interim status', () => {
  test.use({ serviceWorkers: 'block' });

  test('verdict chip renders for any reachable job', async ({ page }) => {
    await page.setViewportSize({ width: 1600, height: 1100 });

    const jobs = await listJobs();
    test.skip(jobs.length === 0, 'No jobs available in workspace');
    const first = jobs[0];

    await page.goto(
      `/?job=${encodeURIComponent(first.id)}&watchPath=${encodeURIComponent(first.watchPath)}`
    );

    // The banner root carries `role="status"`; scope to it so the newer
    // sub-element testids (protocol-verdict-detail / -duration / -signals)
    // do not turn this into a multi-match locator.
    const chip = page.locator('[data-testid^="protocol-verdict-"][role="status"]');
    await expect(chip).toBeVisible({ timeout: 15_000 });

    // One of the three kinds must be present. The exact one depends on the
    // job's current state, which we do not control from this spec.
    const kind = await chip.getAttribute('data-testid');
    expect(kind).toMatch(/^protocol-verdict-(ok|problem|unclear)$/);

    await page.screenshot({
      path: 'test-results/protocol-verdict-chip.png',
      fullPage: false
    });
  });

  test('duration lifts out of the # Status section into a chip on the pill', async ({ page }) => {
    await page.setViewportSize({ width: 1600, height: 1100 });

    const jobs = await listJobs();
    test.skip(jobs.length === 0, 'No jobs available in workspace');
    const target = { id: jobs[0].id, watchPath: jobs[0].watchPath };

    const statusMarkdown = [
      '# Status',
      '',
      '- Result: Success',
      '- Duration: 4 min',
      '',
      '## What Was Done',
      '- Refactored the verdict pill so duration rides on the chip.',
      '',
      '## Notes',
      '- Status header is now lifted out of the rendered body.',
    ].join('\n');

    await installCompletedJobMocks(page, target, statusMarkdown);

    await page.goto(
      `/?job=${encodeURIComponent(target.id)}&watchPath=${encodeURIComponent(target.watchPath)}`,
    );

    // The fixture is in Human Review, so the lane's needs-decision signal
    // legitimately outranks Result: Success. This test owns duration placement,
    // not outcome classification; select the one authoritative banner by role.
    const chip = page.getByTestId('protocol-run-outcome').getByRole('status');
    await expect(chip).toBeVisible({ timeout: 15_000 });

    const duration = page.getByTestId('protocol-verdict-duration');
    await expect(duration).toBeVisible();
    await expect(duration).toContainText('4 min');
    // Icon affordance: a clock glyph sits before the value.
    await expect(duration).toContainText('⏱');

    // The Status section (heading + Result/Duration list) must not appear in
    // the rendered markdown body — that is the whole point of the collapse.
    const body = page.getByTestId('protocol-beautiful-results');
    await expect(body).toBeVisible();
    await expect(body).not.toContainText('Duration:');
    await expect(body).not.toContainText('Result: Success');
    // Sibling sections still render so we know the body itself is alive.
    await expect(body).toContainText('What Was Done');
    await expect(body).toContainText('Refactored the verdict pill');

    await page.screenshot({
      path: 'test-results/protocol-verdict-duration-chip.png',
      fullPage: false,
    });
  });

  test('pipeline failure is the only primary outcome across Result and Pipeline', async ({ page, devBackend }, testInfo) => {
    test.setTimeout(60_000);
    void devBackend;
    await page.setViewportSize({ width: 1600, height: 1100 });
    await page.route('**/api/auth/status', async (route) => {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          profile: 'networked', bootstrapRequired: false, authenticated: true,
          user: {
            id: 'usr_owner', username: 'owner', displayName: 'Owner', role: 'owner',
            projects: [], disabled: false, mustChangePassword: false,
          },
        }),
      });
    });
    const target = { id: 'QS-26-outcome-conflict', watchPath: '/e2e/outcome-conflict' };
    const detail = buildCompletedJobDetail(target.id, target.watchPath, '# Status\n- Result: Partial');
    Object.assign(detail.info, { orchestratorVerdict: 'escalate' });
    await installCompletedJobMocks(page, target, detail.statusMarkdown!, detail);
    await page.route(`**/api/tasks/${encodeURIComponent(target.id)}/pipeline?**`, async (route) => {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          pipeline: {
            id: 'standard', displayName: 'Standard', version: 1, pre: [], core: [],
            post: [{ id: 'post-orchestrator-decision', displayName: 'Final verdict', kind: 'orchestrator', runMode: 'sequential', dependsOn: [], idempotent: true, stub: false }],
          },
          execution: {
            pipelineId: 'standard', pipelineVersion: 1, jobId: target.id, project: 'fixture',
            startedAt: '2026-07-22T10:00:00Z', completedAt: '2026-07-22T10:02:00Z',
            steps: [{ stepId: 'post-orchestrator-decision', kind: 'orchestrator', status: 'failed', durationMs: 1, inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0, verdict: 'escalate', reason: 'environment preparation failed: not a git repository' }],
          },
          cost: null, config: {}, resultFiles: {}, onDemand: null,
        }),
      });
    });
    await page.route('**/api/crash-recovery/pending', async (route) => {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ pending: [] }),
      });
    });

    await page.goto(`/?job=${encodeURIComponent(target.id)}&watchPath=${encodeURIComponent(target.watchPath)}`);

    const banner = page.getByTestId('protocol-run-outcome');
    await expect(page.getByTestId('result-case-badge')).toBeVisible({ timeout: 15_000 });
    await expect(banner).toHaveCount(0);
    await expect(page.getByTestId('result-case-badge')).toHaveAttribute('data-outcome', 'failed');
    await expect(page.getByTestId('result-case-badge')).toContainText('Pipeline failure');
    await expect(page.getByTestId('result-metric-verdict')).toHaveCount(0);
    await expect(page.getByTestId('pane-protocol').getByText('Pipeline failure', { exact: true })).toHaveCount(1);
    await expect(page.getByTestId('overview-failure-reason')).toHaveCount(0);
    await expect(page.getByTestId('protocol-outcome-issue-chip')).toHaveCount(0);
    const decisionPhase = page.locator('[data-testid="overview-pipeline-phase"][data-phase="decision"]');
    if (await decisionPhase.getAttribute('aria-expanded') === 'false') {
      await decisionPhase.evaluate((element: HTMLButtonElement) => element.click());
    }
    const pipelineVerdict = page.getByTestId('overview-pipeline-step-decision');
    await expect(pipelineVerdict).toHaveAttribute('data-verdict', 'failed');
    await expect(pipelineVerdict).toContainText('Pipeline failure');

    const evidenceDir = resolve(process.env.JOB_RESULTS_DIR ?? join('..', 'results', 'AGT-2425'));
    mkdirSync(evidenceDir, { recursive: true });
    await page.getByTestId('pane-protocol').screenshot({
      path: join(evidenceDir, 'card-detail-after-single-pipeline-failure.png'),
    });

    await testInfo.attach('authoritative-run-outcome', {
      body: await page.screenshot({ fullPage: false }),
      contentType: 'image/png',
    });
  });

  test('review activity without a verdict stays actionable from Result', async ({ page }, testInfo) => {
    await page.setViewportSize({ width: 1600, height: 1100 });

    const jobs = await listJobs();
    test.skip(jobs.length === 0, 'No jobs available in workspace');
    const target = { id: jobs[0].id, watchPath: jobs[0].watchPath };
    const detail = buildCompletedJobDetail(target.id, target.watchPath, null);
    detail.summaryState = {
      status: 'none',
      startedAt: null,
      finishedAt: null,
      errorMessage: null,
    };

    await page.route(`**/api/tasks/${encodeURIComponent(target.id)}/summary/regenerate?**`, async (route) => {
      await route.fulfill({ status: 202, contentType: 'application/json', body: '{}' });
    });
    await page.route((url) => url.pathname === `/api/tasks/${encodeURIComponent(target.id)}`, async (route) => {
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(detail) });
    });
    await page.route(`**/api/tasks/${encodeURIComponent(target.id)}/output?**`, async (route) => {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify([
          {
            timestamp: '2026-07-12T10:00:00Z',
            stream: 'stdout',
            text: 'Investigated the review hand-off and left useful activity.',
          },
        ]),
      });
    });
    await page.route(`**/api/tasks/${encodeURIComponent(target.id)}/runs?**`, async (route) => {
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ runs: [] }) });
    });
    await page.route(`**/api/tasks/${encodeURIComponent(target.id)}/session-events?**`, async (route) => {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ events: [], sessionChain: [] }),
      });
    });
    await page.route(`**/api/tasks/${encodeURIComponent(target.id)}/claude-session?**`, async (route) => {
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(null) });
    });

    await page.goto(
      `/?job=${encodeURIComponent(target.id)}&watchPath=${encodeURIComponent(target.watchPath)}`,
    );

    const resultTab = page.getByTestId('inspector-tab-protocol');
    await expect(resultTab).toBeVisible({ timeout: 15_000 });
    await expect(resultTab).toBeEnabled();
    await expect(resultTab).toHaveClass(/pane-tab--active/);

    const generate = page.getByTestId('protocol-regenerate-summary');
    await expect(generate).toBeVisible();
    await expect(generate).toBeEnabled();
    await expect(generate).toContainText('Generate result');
    await testInfo.attach('verdictless-review-result', {
      body: await page.getByTestId('pane-protocol').screenshot(),
      contentType: 'image/png',
    });

    const request = page.waitForRequest((candidate) =>
      candidate.method() === 'POST' && candidate.url().includes('/summary/regenerate'),
    );
    await generate.evaluate((element: HTMLButtonElement) => element.click());
    await request;
  });

  test('interim endpoint returns the precondition error when cli-output.log is missing', async ({ page }) => {
    const watchPaths = await api<WatchPathEntry[]>('/api/watch-paths');
    test.skip(watchPaths.length === 0, 'No watch paths configured');
    const watchPath = watchPaths[0].path;

    const slug = `e2e-interim-precondition-${Date.now()}`;
    const created = await createJob({
      id: slug,
      title: 'E2E interim precondition',
      watchPath,
      agent: 'claude',
      cliType: 'claude',
      promptMarkdown: 'placeholder',
      targetState: '1-preparation'
    });

    try {
      const res = await page.request.post(
        `${BACKEND}/api/tasks/${encodeURIComponent(created.id)}/summary/interim?watchPath=${encodeURIComponent(watchPath)}`,
        { headers: { 'x-client-id': 'local-default' } }
      );
      // The endpoint surfaces precondition errors as 400 with `{ error: "..." }`.
      expect(res.status()).toBe(400);
      const body = await res.json();
      expect(body.error).toMatch(/CLI output|cli-output\.log/i);
    } finally {
      await api(
        `/api/tasks/${encodeURIComponent(created.id)}?watchPath=${encodeURIComponent(watchPath)}`,
        { method: 'DELETE' }
      ).catch(() => { /* best-effort cleanup */ });
    }
  });

  /**
   * AGT-2813 / guideline rule ADM-17. The status line is the ONE disclosure
   * control of the result header: it carries the shared marker and
   * `aria-expanded`, and the "Why this status?" row that used to duplicate the
   * same expansion is gone. The first assertion holds for every job; the
   * toggle behaviour is exercised whenever the current verdict is expandable
   * (a long reason or raw signals), which is a property of the job, not of
   * this spec.
   */
  test('the status line is the only disclosure control of the result header', async ({ page }) => {
    await page.setViewportSize({ width: 1600, height: 1100 });

    const jobs = await listJobs();
    test.skip(jobs.length === 0, 'No jobs available in workspace');
    const first = jobs[0];

    await page.goto(
      `/?job=${encodeURIComponent(first.id)}&watchPath=${encodeURIComponent(first.watchPath)}`
    );

    const chip = page.locator('[data-testid^="protocol-verdict-"][role="status"]');
    await expect(chip).toBeVisible({ timeout: 15_000 });

    // The duplicated second control must never come back.
    await expect(page.getByTestId('protocol-verdict-signals-toggle')).toHaveCount(0);

    const line = chip.getByTestId('protocol-verdict-detail');
    await expect(line).toBeVisible();

    if ((await line.evaluate((el) => el.tagName)) !== 'BUTTON') {
      // A short reason with no raw signals is deliberately not expandable.
      await expect(line).not.toHaveAttribute('aria-expanded', /.*/);
      return;
    }

    const marker = line.locator('app-disclosure-marker');
    await expect(marker).toHaveCount(1);
    await expect(line).toHaveAttribute('aria-expanded', 'false');
    await expect(marker).not.toHaveClass(/studio-disclosure__marker--open/);
    await page.screenshot({ path: 'test-results/status-header-collapsed.png', fullPage: false });

    await line.click();
    await expect(line).toHaveAttribute('aria-expanded', 'true');
    await expect(marker).toHaveClass(/studio-disclosure__marker--open/);
    await page.screenshot({ path: 'test-results/status-header-expanded.png', fullPage: false });

    await line.click();
    await expect(line).toHaveAttribute('aria-expanded', 'false');
  });

});
