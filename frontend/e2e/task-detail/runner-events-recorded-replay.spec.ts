import { expect, Page, test } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';

interface RecordedReplay {
  target: { id: string; watchPath: string };
  output: { timestamp: string; stream: string; text: string }[];
  runnerEvents: Record<string, unknown>[];
}

const RECORDED = JSON.parse(fs.readFileSync(
  path.resolve(__dirname, '../fixtures/recorded-runs/agt-2149-typed-replay.json'),
  'utf8',
)) as RecordedReplay;
const RESULTS_DIR = process.env.JOB_RESULTS_DIR?.trim() || '';

function detail() {
  const { id, watchPath } = RECORDED.target;
  return {
    info: {
      id,
      taskKey: 'AGT-2149',
      displayKey: 'AGT-2149',
      title: 'Recorded CAC replay rendering',
      state: '5-human-review',
      order: 1,
      agent: 'codex',
      cliType: 'codex',
      model: 'gpt-5.4-mini',
      watchPath,
      projectName: 'fixture',
      folderPath: `${watchPath}/.orchestrator/jobs/5-human-review/${id}`,
      createdAt: '2026-07-20T08:00:00.000Z',
      lastActivity: '2026-07-20T08:06:54.000Z',
      sessionName: 'sess-agt-2149-7f3a',
      useOwnSession: false,
      lastUsage: null,
      execution: null,
      commit: null,
      commits: [],
      codeActivityDetected: true,
      summaryState: null,
      taskType: 'feature',
      tags: ['frontend'],
      tokenSummary: {
        calls: 3,
        inputTokens: 100000,
        outputTokens: 20000,
        cacheReadTokens: 0,
        cacheCreationTokens: 0,
        totalTokens: 120000,
        lastModel: 'gpt-5.4-mini',
        lastUpdate: '2026-07-20T08:06:54.000Z',
        entries: [],
      },
      references: { dependsOn: [], relatedTo: [], blockedBy: [], supersedes: [] },
    },
    promptMarkdown: 'Replay the recorded implementation and pipeline run.',
    promptHistory: [],
    titleHistory: [],
    statusMarkdown: '## Status\n\nImplementation complete. Pipeline post-processing.',
    contextUsage: null,
    log: [],
    summaryState: null,
    reviewEvidence: [],
  };
}

function timeline() {
  return {
    runCount: 1,
    firstStartedAt: '2026-07-20T08:00:00.000Z',
    lastActivityAt: '2026-07-20T08:06:54.000Z',
    hasActiveRun: false,
    runs: [{
      index: 1,
      intent: 'start',
      startedAt: '2026-07-20T08:00:00.000Z',
      endedAt: '2026-07-20T08:06:54.000Z',
      status: 'completed',
      cli: 'codex',
      exitCode: 0,
      durationSeconds: 414,
      inputSessionId: null,
      capturedSessionId: 'sess-agt-2149-7f3a',
      resumed: false,
      reason: null,
      userFollowup: null,
      lineStart: 1,
      lineEnd: RECORDED.output.length,
      headShaBefore: '1111111111111111111111111111111111111111',
      headShaAfter: '2222222222222222222222222222222222222222',
      contextRef: null,
    }],
    promptEntries: [],
    runnerEvents: RECORDED.runnerEvents,
  };
}

async function installRoutes(page: Page): Promise<void> {
  const { id, watchPath } = RECORDED.target;
  const esc = encodeURIComponent(id);
  await page.route('**/api/**', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: '[]',
  }));
  await page.route('**/api/v1/studio/auth/status', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ profile: 'local', bootstrapRequired: false, authenticated: true }),
  }));
  await page.route('**/api/v1/studio/board**', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
      failedPickup: [], codeNotComplete: [], autoReview: [], humanReview: [detail().info],
      review: [], completed: [], archive: [],
    }),
  }));
  await page.route('**/api/watch-paths**', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify([{ name: 'fixture', path: watchPath, rootPath: watchPath }]),
  }));
  await page.route('**/api/v1/studio/runner/status**', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ projects: { fixture: { projectName: 'fixture', mode: 'manual', activeJobId: null, activeExecution: null, queuedJobIds: [] } } }),
  }));
  await page.route('**/api/cli/quota**', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ snapshots: [], ttlSeconds: 600 }),
  }));
  await page.route(`**/api/tasks/${esc}/output?**`, route => route.fulfill({
    status: 200, contentType: 'application/json', body: JSON.stringify(RECORDED.output),
  }));
  await page.route(`**/api/tasks/${esc}/pipeline?**`, route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      pipeline: { pre: [], core: [], post: [], allSteps: [] },
      execution: null,
      executions: [],
      config: {},
      cost: null,
    }),
  }));
  await page.route(`**/api/tasks/${esc}/runs?**`, route => route.fulfill({
    status: 200, contentType: 'application/json', body: JSON.stringify(timeline()),
  }));
  await page.route(`**/api/tasks/${esc}/session-events?**`, route => route.fulfill({
    status: 200, contentType: 'application/json', body: JSON.stringify({ events: [], sessionChain: [] }),
  }));
  await page.route(`**/api/tasks/${esc}/claude-session?**`, route => route.fulfill({
    status: 200, contentType: 'application/json', body: 'null',
  }));
  await page.route(`**/api/v1/projects/*/tasks/${esc}?**`, route => route.fulfill({
    status: 200, contentType: 'application/json', body: JSON.stringify(detail()),
  }));
}

test('recorded run renders typed replay metadata, trace diagnostics, and a stable pinned bottom', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 520 });
  await page.addInitScript(() => {
    localStorage.setItem('atp.flag.nextGenChat', '1');
    localStorage.setItem('taskboard.panesVisible', JSON.stringify({ prompt: true, protocol: true, git: false }));
  });
  await installRoutes(page);

  const { id, watchPath } = RECORDED.target;
  await page.goto(`/?job=${encodeURIComponent(id)}&watchPath=${encodeURIComponent(watchPath)}`);
  await page.getByTestId('inspector-tab-activity').click();

  const activity = page.getByTestId('activity-panel');
  const conversation = activity.getByTestId('conversation-view');
  await expect(conversation).toBeVisible({ timeout: 20_000 });
  await expect(conversation).toContainText('Turn completed');
  await expect(conversation).toContainText('Session completed');
  await expect(conversation).not.toContainText('Turn completed (tokens:');
  await expect(conversation).not.toContainText('PATH did not include');
  await expect(conversation).not.toContainText('Optional replay plugin');

  const metadata = activity.getByTestId('runner-replay-metadata');
  await expect(metadata).toContainText('sess-agt-2149-7f3a');
  await expect(metadata).toContainText('gpt-5.4-mini');
  await expect(metadata).toContainText('high');
  await expect(metadata).toContainText('6m 52s');
  await expect(metadata).toContainText('74,192 tokens');
  await expect(metadata).toContainText('8,331 tokens');
  await expect(activity.getByTestId('runner-replay-implementation').first()).toContainText('completed');
  await expect(activity.getByTestId('runner-replay-pipeline').first()).toContainText('post-processing');

  const metrics = activity.getByTestId('conversation-metric-token');
  await expect(metrics).toHaveCount(2);
  await expect(metrics.filter({ hasText: 'task' })).toContainText('120k tok');
  await expect(metrics.filter({ hasText: 'turn' })).toContainText('83k tok');

  const scrollSamples = await conversation.evaluate(async element => {
    let scroller: HTMLElement | null = element as HTMLElement;
    while (scroller && !/auto|scroll|overlay/.test(getComputedStyle(scroller).overflowY)) {
      scroller = scroller.parentElement;
    }
    if (!scroller) throw new Error('No Activity transcript scroller found');
    scroller.scrollTop = scroller.scrollHeight;
    const values: number[] = [];
    for (let frame = 0; frame < 12; frame += 1) {
      await new Promise<void>(resolve => requestAnimationFrame(() => resolve()));
      values.push(scroller.scrollTop);
    }
    return {
      values,
      stuck: element.getAttribute('data-stuck'),
      clientHeight: scroller.clientHeight,
      scrollHeight: scroller.scrollHeight,
    };
  });
  expect(scrollSamples.scrollHeight).toBeGreaterThan(scrollSamples.clientHeight);
  expect(scrollSamples.values[0]).toBeGreaterThan(0);
  expect(scrollSamples.stuck).toBe('true');
  expect(Math.max(...scrollSamples.values) - Math.min(...scrollSamples.values)).toBe(0);
  await expect(activity.getByTestId('conversation-jump-latest')).toHaveCount(0);

  if (RESULTS_DIR) {
    for (const theme of ['light', 'dark'] as const) {
      await page.evaluate(value => {
        document.documentElement.dataset['studioTheme'] = value;
        localStorage.setItem('atp.studio.theme', value);
      }, theme);
      await page.screenshot({
        path: path.join(RESULTS_DIR, `agt-2149-typed-runner-replay-${theme}.png`),
        fullPage: false,
      });
    }
  }

  await page.getByTestId('activity-toolbar-menu').click();
  await page.getByTestId('activity-toolbar-menu-item-trace').click();
  const trace = activity.getByTestId('activity-log-trace');
  await expect(trace).toContainText('[cli-path] PATH did not include the configured Codex directory');
  await expect(trace).toContainText('[plugin-load] Optional replay plugin could not be loaded');
  if (RESULTS_DIR) {
    await page.screenshot({ path: path.join(RESULTS_DIR, 'agt-2149-runner-diagnostics-trace.png'), fullPage: false });
  }
});
