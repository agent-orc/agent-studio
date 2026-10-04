import { expect, Page, test } from '@playwright/test';
import * as path from 'path';
import { listJobs } from '../helpers/jobs';

/**
 * Slice 1 of the embedded chat integration plan
 * (`docs/research/embedded-chat-integration-2026-05.md`): the task-detail
 * Activity tab host adapter.
 *
 * When `Frontend:NextGenChat` is off the Activity tab must render exactly as
 * before (legacy `app-activity-log-view`). When the flag is on it must render
 * the new `app-conversation-view` against the shared `ConversationEvent[]`
 * projection, with a Trace button that flips the body back to the legacy
 * view without losing the raw log.
 *
 * The spec stubs the same endpoint set the Verbose Debug spec uses so the
 * Activity tab has deterministic conversation evidence (user prompt,
 * orchestrator reissue decision, tool burst, watchdog wait, agent reply,
 * sentinel) without needing a live backend roundtrip.
 *
 * Screenshots land under the running task's `results/` folder per the
 * project doctrine; `test-results/` is scratch.
 */

const RESULTS_DIR = path.resolve(
  __dirname,
  '../../../../agent-taskboard-workspace/projects/agent-taskboard/3-progress/integrate-creative-design-mockup/results'
);

interface OutLine { timestamp: string; stream: string; text: string; }

function buildOutputBuffer(): OutLine[] {
  const t0 = Date.now() - 10 * 60 * 1000;
  const t = (offsetSec: number) => new Date(t0 + offsetSec * 1000).toISOString();
  return [
    { timestamp: t(0),  stream: 'user',         text: 'Investigate the failing protocol parser regression.' },
    { timestamp: t(2),  stream: 'orchestrator', text: '[orchestrator] decision: reissue task - heuristic outcome did not match sentinel grammar.' },
    { timestamp: t(5),  stream: 'stdout',       text: 'Reading prompt.md to understand the parser grammar.' },
    { timestamp: t(7),  stream: 'stdout',       text: '* Read src/components/activity-log.parser.ts' },
    { timestamp: t(7),  stream: 'stdout',       text: '  | activity-log.parser.ts (1-180)' },
    { timestamp: t(8),  stream: 'stdout',       text: '* Read src/components/activity-log.parser.spec.ts' },
    { timestamp: t(9),  stream: 'stdout',       text: '* Search "sentinel"' },
    { timestamp: t(10), stream: 'stdout',       text: '  | matches in 4 files' },
    { timestamp: t(11), stream: 'stdout',       text: '* Edit src/components/activity-log.parser.ts' },
    { timestamp: t(12), stream: 'stdout',       text: '  | adding heuristic guard' },
    { timestamp: t(13), stream: 'stdout',       text: '* Run npm --prefix frontend run test' },
    { timestamp: t(14), stream: 'stdout',       text: '  | exit 0 - 108 passed' },
    { timestamp: t(70), stream: 'orchestrator', text: '[watchdog] agent quiet for 60s, allowing one more window' },
    { timestamp: t(95), stream: 'stdout',       text: 'Wrote regression test and confirmed parser handles the sentinel correctly.' },
    { timestamp: t(98), stream: 'stdout',       text: '[[TASK_DONE]]' }
  ];
}

function buildJobDetail(jobId: string, watchPath: string) {
  const startedAt = new Date(Date.now() - 12 * 60 * 1000).toISOString();
  return {
    info: {
      id: jobId,
      jobKey: `${watchPath}::${jobId}`,
      title: 'Next-gen chat host adapter fixture',
      state: '4-auto-review',
      agent: 'claude',
      cliType: 'claude',
      model: 'claude-opus-4-7',
      watchPath,
      projectName: 'fixture',
      folderPath: `${watchPath}/.orchestrator/jobs/4-auto-review/${jobId}`,
      sessionName: '00000000-0000-0000-0000-000000000000',
      lastUsage: null,
      execution: null,
      tokenSummary: {
        calls: 4,
        inputTokens: 18_400,
        outputTokens: 6_120,
        cacheReadTokens: 11_900,
        cacheCreationTokens: 980,
        totalTokens: 37_400,
        lastModel: 'claude-haiku-4-5',
        lastUpdate: new Date().toISOString(),
        entries: []
      },
      order: 1,
      lastActivity: new Date().toISOString(),
      createdAt: startedAt,
      useOwnSession: false,
      commit: null
    },
    promptMarkdown: 'Investigate the failing protocol parser regression.',
    statusMarkdown: null,
    log: [],
    promptHistory: [],
    summaryState: { status: 'none', startedAt: null, finishedAt: null, errorMessage: null }
  };
}

function buildRunTimeline() {
  const startedAt = new Date(Date.now() - 12 * 60 * 1000).toISOString();
  const endedAt = new Date(Date.now() - 8 * 60 * 1000).toISOString();
  return {
    runCount: 1,
    firstStartedAt: startedAt,
    lastActivityAt: endedAt,
    hasActiveRun: false,
    runs: [
      {
        index: 1,
        intent: 'start',
        startedAt,
        endedAt,
        status: 'completed',
        cli: 'claude',
        exitCode: 0,
        durationSeconds: 180,
        inputSessionId: null,
        capturedSessionId: 'sess-001',
        resumed: false,
        reason: null,
        userFollowup: null,
        lineStart: 1,
        lineEnd: 15,
        headShaBefore: null,
        headShaAfter: null
      }
    ]
  };
}

async function pickAnyJob(): Promise<{ id: string; watchPath: string } | null> {
  const jobs = await listJobs();
  if (jobs.length === 0) return null;
  return { id: jobs[0].id, watchPath: jobs[0].watchPath };
}

async function installMocks(
  page: Page,
  target: { id: string; watchPath: string }
): Promise<void> {
  const detailBody = JSON.stringify(buildJobDetail(target.id, target.watchPath));
  const outputBody = JSON.stringify(buildOutputBuffer());
  const runsBody = JSON.stringify(buildRunTimeline());

  const escId = encodeURIComponent(target.id);
  await page.route(`**/api/v1/projects/*/tasks/${escId}?**`, async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: detailBody });
  });
  await page.route(`**/api/tasks/${escId}/output?**`, async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: outputBody });
  });
  await page.route(`**/api/tasks/${escId}/runs?**`, async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: runsBody });
  });
  await page.route(`**/api/tasks/${escId}/screenshots?**`, async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ jobId: target.id, screenshots: [] }) });
  });
  await page.route(`**/api/tasks/${escId}/session-events?**`, async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ events: [], sessionChain: [], currentSessionId: null }) });
  });
  await page.route(`**/api/tasks/${escId}/claude/session-info?**`, async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ sessionInfo: null, rateLimit: null }) });
  });
  await page.route(`**/api/tasks/${escId}/git/status?**`, async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ isRepo: false, branch: null, filesChanged: 0, totalAdded: 0, totalRemoved: 0, files: [], error: null }) });
  });
}

async function setFlag(page: Page, on: boolean): Promise<void> {
  // `nextGenChat` is default-ON now: a missing key reads as opt-in, so the
  // off-state must be written explicitly as '0' (mirrors writeExplicit in
  // FeatureFlagsService) rather than removing the key.
  await page.addInitScript((enable) => {
    localStorage.setItem('atp.flag.nextGenChat', enable ? '1' : '0');
  }, on);
}

test.describe('Next-gen chat task host adapter (Frontend:NextGenChat)', () => {
  test('flag off renders the legacy activity-log view byte-stably', async ({ page }) => {
    const target = await pickAnyJob();
    if (!target) {
      test.skip(true, 'No jobs on the board to attach mocks to.');
      return;
    }
    await setFlag(page, false);
    await installMocks(page, target);
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto(`/?job=${encodeURIComponent(target.id)}&watchPath=${encodeURIComponent(target.watchPath)}`);

    const activityTab = page.getByTestId('inspector-tab-activity');
    await expect(activityTab).toBeVisible({ timeout: 10_000 });
    await activityTab.click();

    // Off-state: legacy activity log present, new conversation view absent.
    const conv = page.getByTestId('conversation-view');
    const fallbackBanner = page.getByTestId('next-gen-chat-trace-banner');
    await expect(conv).toHaveCount(0);
    await expect(fallbackBanner).toHaveCount(0);

    // The Verbose Debug button and run timeline are still in scope.
    await expect(page.getByTestId('protocol-open-verbose-debug')).toBeVisible();

    await page.screenshot({
      path: path.join(RESULTS_DIR, 'activity-tab-flag-off.png'),
      fullPage: false
    });
  });

  test('flag on swaps the body to app-conversation-view with actor labels and a tool-burst chip', async ({ page }) => {
    const target = await pickAnyJob();
    if (!target) {
      test.skip(true, 'No jobs on the board to attach mocks to.');
      return;
    }
    await setFlag(page, true);
    await installMocks(page, target);
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto(`/?job=${encodeURIComponent(target.id)}&watchPath=${encodeURIComponent(target.watchPath)}`);

    const activityTab = page.getByTestId('inspector-tab-activity');
    await expect(activityTab).toBeVisible({ timeout: 10_000 });
    await activityTab.click();

    const conv = page.getByTestId('conversation-view');
    await expect(conv).toBeVisible({ timeout: 10_000 });

    // The conversation feed surfaces user, agent, and tool-burst rows.
    await expect(conv.getByTestId('conversation-feed')).toBeVisible();
    await expect(conv.getByTestId('conversation-message-message.user')).toBeVisible();
    await expect(conv.getByTestId('conversation-tool-burst').first()).toBeVisible();

    // Trace button and Debug button render in the header.
    await expect(conv.getByTestId('conversation-open-trace')).toBeVisible();
    await expect(conv.getByTestId('conversation-open-verbose-debug')).toBeVisible();

    // Run timeline still renders alongside the conversation view (slice 1 keeps it).
    await expect(page.getByTestId('protocol-open-verbose-debug')).toBeVisible();

    await page.screenshot({
      path: path.join(RESULTS_DIR, 'activity-tab-flag-on-conversation.png'),
      fullPage: false
    });
  });

  test('Trace button flips the body back to the legacy view and Back returns to conversation', async ({ page }) => {
    const target = await pickAnyJob();
    if (!target) {
      test.skip(true, 'No jobs on the board to attach mocks to.');
      return;
    }
    await setFlag(page, true);
    await installMocks(page, target);
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto(`/?job=${encodeURIComponent(target.id)}&watchPath=${encodeURIComponent(target.watchPath)}`);

    const activityTab = page.getByTestId('inspector-tab-activity');
    await expect(activityTab).toBeVisible({ timeout: 10_000 });
    await activityTab.click();

    const conv = page.getByTestId('conversation-view');
    await expect(conv).toBeVisible({ timeout: 10_000 });

    // Click "Trace": the conversation view yields to the trace banner +
    // legacy activity-log view; raw log access is one click away.
    await conv.getByTestId('conversation-open-trace').click();
    await expect(page.getByTestId('next-gen-chat-trace-banner')).toBeVisible();
    await expect(page.getByTestId('conversation-view')).toHaveCount(0);

    await page.screenshot({
      path: path.join(RESULTS_DIR, 'activity-tab-flag-on-trace-fallback.png'),
      fullPage: false
    });

    // Back: returns to the conversation view without losing the flag state.
    await page.getByTestId('next-gen-chat-trace-back').click();
    await expect(page.getByTestId('conversation-view')).toBeVisible();
    await expect(page.getByTestId('next-gen-chat-trace-banner')).toHaveCount(0);
  });

  test('Debug button opens the Verbose Debug overlay without leaving the conversation host', async ({ page }) => {
    const target = await pickAnyJob();
    if (!target) {
      test.skip(true, 'No jobs on the board to attach mocks to.');
      return;
    }
    await setFlag(page, true);
    await installMocks(page, target);
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto(`/?job=${encodeURIComponent(target.id)}&watchPath=${encodeURIComponent(target.watchPath)}`);

    const activityTab = page.getByTestId('inspector-tab-activity');
    await expect(activityTab).toBeVisible({ timeout: 10_000 });
    await activityTab.click();

    const conv = page.getByTestId('conversation-view');
    await expect(conv).toBeVisible({ timeout: 10_000 });
    await conv.getByTestId('conversation-open-verbose-debug').click();

    await expect(page.getByTestId('verbose-debug-overlay')).toBeVisible({ timeout: 5_000 });

    // Closing the overlay leaves the conversation host visible.
    await page.keyboard.press('Escape');
    await expect(page.getByTestId('verbose-debug-overlay')).toHaveCount(0);
    await expect(page.getByTestId('conversation-view')).toBeVisible();
  });
});
