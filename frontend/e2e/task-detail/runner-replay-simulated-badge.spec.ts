import { expect, Page, test } from '@playwright/test';
import * as path from 'path';

// AGT-W34 slice S3. A public-demo instance replays a signed fixed trace through
// a narrow server scope. Nothing the visitor sees was executed, so every
// replayed runner event has to say Simulated on the surface that renders it.

const TASK_ID = 'stream-export-progress-to-the-activity-feed';
const WATCH_PATH = '/tmp/demo-app';
const RESULTS_DIR = process.env.JOB_RESULTS_DIR?.trim() || '';

const SIMULATED_EVENTS = [
  {
    id: 'replay:1:1', kind: 'session.started', timestamp: '2026-08-09T08:00:00.000Z',
    origin: 'simulated', sessionId: 'demo-session-12', runIndex: 1,
    cli: 'claude', model: 'claude-opus-4-8', thinkingLevel: 'medium',
    message: 'Simulated session opened for the export progress stream',
  },
  {
    id: 'replay:1:3', kind: 'turn.completed', timestamp: '2026-08-09T08:01:36.000Z',
    origin: 'simulated', sessionId: 'demo-session-12', turnId: 'demo-turn-12-1', runIndex: 1,
    durationMs: 84_000, inputTokens: 41_200, outputTokens: 5_300, reasoningTokens: 1_800,
    message: 'Mapped the progress events onto the feed projection',
  },
  {
    id: 'replay:1:9', kind: 'session.completed', timestamp: '2026-08-09T08:06:18.000Z',
    origin: 'simulated', sessionId: 'demo-session-12', runIndex: 1,
    durationMs: 378_000, inputTokens: 142_100, outputTokens: 18_800,
    message: 'Simulated session closed',
  },
];

function info() {
  return {
    id: TASK_ID,
    taskKey: 'DEMO-12',
    displayKey: 'DEMO-12',
    title: 'Stream export progress to the activity feed',
    state: '3-progress',
    order: 1,
    agent: 'claude',
    cliType: 'claude',
    model: 'claude-opus-4-8',
    watchPath: WATCH_PATH,
    projectName: 'Demo App',
    folderPath: `${WATCH_PATH}/3-progress/DEMO-12`,
    createdAt: '2026-08-09T08:00:00.000Z',
    lastActivity: '2026-08-09T08:06:18.000Z',
    sessionName: 'demo-session-12',
    useOwnSession: false,
    lastUsage: null,
    execution: null,
    commit: null,
    commits: [],
    codeActivityDetected: false,
    summaryState: null,
    taskType: 'feature',
    tags: ['demo'],
    tokenSummary: {
      calls: 3,
      inputTokens: 142_100,
      outputTokens: 18_800,
      cacheReadTokens: 0,
      cacheCreationTokens: 0,
      totalTokens: 160_900,
      lastModel: 'claude-opus-4-8',
      lastUpdate: '2026-08-09T08:06:18.000Z',
      entries: [],
    },
    references: { dependsOn: [], relatedTo: [], blockedBy: [], supersedes: [] },
  };
}

function timeline() {
  return {
    runCount: 1,
    firstStartedAt: '2026-08-09T08:00:00.000Z',
    lastActivityAt: '2026-08-09T08:06:18.000Z',
    hasActiveRun: false,
    runs: [{
      index: 1,
      intent: 'start',
      startedAt: '2026-08-09T08:00:00.000Z',
      endedAt: '2026-08-09T08:06:18.000Z',
      status: 'completed',
      cli: 'claude',
      exitCode: 0,
      durationSeconds: 378,
      inputSessionId: null,
      capturedSessionId: 'demo-session-12',
      resumed: false,
      reason: null,
      userFollowup: null,
      lineStart: 1,
      lineEnd: 1,
      headShaBefore: null,
      headShaAfter: null,
      contextRef: null,
    }],
    promptEntries: [],
    runnerEvents: SIMULATED_EVENTS,
  };
}

async function installRoutes(page: Page): Promise<void> {
  const esc = encodeURIComponent(TASK_ID);
  await page.route('**/api/**', route => route.fulfill({
    status: 200, contentType: 'application/json', body: '[]',
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
      backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [info()],
      failedPickup: [], codeNotComplete: [], autoReview: [], humanReview: [],
      review: [], completed: [], archive: [],
    }),
  }));
  await page.route('**/api/watch-paths**', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify([{ name: 'Demo App', path: WATCH_PATH, rootPath: WATCH_PATH }]),
  }));
  await page.route('**/api/v1/studio/runner/status**', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      projects: {
        'Demo App': {
          projectName: 'Demo App', mode: 'manual', activeJobId: null,
          activeExecution: null, queuedJobIds: [],
        },
      },
    }),
  }));
  await page.route('**/api/cli/quota**', route => route.fulfill({
    status: 200, contentType: 'application/json', body: JSON.stringify({ snapshots: [], ttlSeconds: 600 }),
  }));
  await page.route('**/api/projects/*/workbenches**', route => route.fulfill({
    status: 200, contentType: 'application/json', body: JSON.stringify({ items: [] }),
  }));
  await page.route(`**/api/tasks/${esc}/output?**`, route => route.fulfill({
    status: 200, contentType: 'application/json', body: '[]',
  }));
  await page.route(`**/api/tasks/${esc}/pipeline?**`, route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      pipeline: { pre: [], core: [], post: [], allSteps: [] },
      execution: null, executions: [], config: {}, cost: null,
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
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      info: info(),
      promptMarkdown: 'Pinned public-demo fixture.',
      promptHistory: [], titleHistory: [],
      statusMarkdown: '', contextUsage: null, log: [],
      summaryState: null, reviewEvidence: [],
    }),
  }));
}

test('replayed public-demo runner events are labelled Simulated in both themes', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 720 });
  await page.addInitScript(() => {
    localStorage.setItem('atp.flag.nextGenChat', '1');
    localStorage.setItem('taskboard.panesVisible', JSON.stringify({ prompt: true, protocol: true, git: false }));
  });
  await installRoutes(page);

  await page.goto(`/?job=${encodeURIComponent(TASK_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
  await page.getByTestId('inspector-tab-activity').click();

  const activity = page.getByTestId('activity-panel');
  const metadata = activity.getByTestId('runner-replay-metadata');
  await expect(metadata).toBeVisible({ timeout: 20_000 });

  // The section says it once, and every replayed turn says it again.
  await expect(activity.getByTestId('runner-replay-simulated')).toHaveText('Simulated');
  await expect(activity.getByTestId('runner-replay-simulated-replay:1:3')).toHaveText('Simulated');
  await expect(activity.getByTestId('runner-replay-simulated-replay:1:9')).toHaveText('Simulated');

  // The transcript carries the same marker, so a visitor reading only the feed
  // still cannot mistake the scene for work that happened.
  const conversation = activity.getByTestId('conversation-view');
  await expect(conversation).toContainText('Simulated turn completed');
  await expect(conversation).toContainText('Simulated session completed');

  for (const theme of ['light', 'dark'] as const) {
    await page.evaluate(value => {
      document.documentElement.dataset['studioTheme'] = value;
      localStorage.setItem('atp.studio.theme', value);
    }, theme);
    await expect(activity.getByTestId('runner-replay-simulated')).toBeVisible();
    if (RESULTS_DIR) {
      await page.screenshot({
        path: path.join(RESULTS_DIR, `demo-replay-simulated-${theme}.png`),
        fullPage: false,
      });
    }
  }
});
