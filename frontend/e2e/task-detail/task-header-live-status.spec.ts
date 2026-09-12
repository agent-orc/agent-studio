import { expect, Page, test } from '@playwright/test';
import * as path from 'path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

const PROJECT = 'fixture';
const WATCH_PATH = 'C:/fixtures/task-header-live-status';
const JOB_ID = 'task-header-live-status';
const RESULTS_DIR = process.env.JOB_RESULTS_DIR ?? '';
const LEGACY_FLOATING_STATUS_CSS = `
  .task-live.task-live--detail {
    margin: 0 var(--studio-spacing-4) var(--studio-spacing-3) !important;
    padding: var(--studio-spacing-3) var(--studio-spacing-4) !important;
    border: 1px solid color-mix(in srgb, var(--studio-accent-4) 34%, var(--studio-border)) !important;
    border-radius: 10px !important;
    background: color-mix(in srgb, var(--studio-accent-4) 9%, var(--studio-bg-elevated)) !important;
  }
`;

function pipelineStep() {
  return {
    id: 'pre-loop-guard',
    displayName: 'Loop check',
    kind: 'module',
    runMode: 'sequential',
    dependsOn: [],
    idempotent: true,
    stub: false,
  };
}

function taskDetail(withCandidates = false) {
  return {
    info: {
      id: JOB_ID,
      key: 'FIXTURE-1',
      taskKey: `${WATCH_PATH}::${JOB_ID}`,
      title: 'Task header live status fixture',
      state: '2-ready',
      order: 1,
      agent: 'codex',
      cliType: 'codex',
      model: 'gpt-5.4',
      thinkingLevel: 'high',
      betterCandidates: withCandidates ? [{
        model: 'gpt-5.6-terra',
        effort: 'medium',
        benchmarkType: 'swe-bench-verified',
        benchmarkName: 'SWE-bench Verified',
        scoreDelta: 4.2,
        costDeltaUsd: -0.184,
        evidenceAgeDays: 1,
        evidenceStale: false,
        evidenceSnapshot: 'v1:2026-09-11:2',
        matrixUrl: 'https://agent-orchestrator.dev/token-economy/model-benchmarks/',
        note: 'gpt-5.6-terra / medium: SWE-bench Verified, score +4.2, cost -$0.184, evidence 1d old',
      }] : [],
      createdAt: '2026-08-11T09:40:00Z',
      lastActivity: '2026-08-11T09:58:00Z',
      watchPath: WATCH_PATH,
      projectName: PROJECT,
      folderPath: `${WATCH_PATH}/.orchestrator/jobs/2-ready/${JOB_ID}`,
      sessionName: null,
      lastUsage: null,
      execution: null,
      commit: null,
      commits: [],
      ownerClientId: 'local-default',
      liveStatus: {
        attempt: 1,
        activeStep: null,
        nextSteps: [{ stepId: 'pre-loop-guard', displayName: 'Loop check' }],
        queue: { kind: 'runner', position: 3 },
        latestEventAt: '2026-08-11T09:58:00Z',
      },
    },
    promptMarkdown: 'Task-header status fixture.',
    statusMarkdown: '',
    log: [],
    promptHistory: [],
    contextUsage: null,
    reviewEvidence: [],
    summaryState: {
      status: 'none',
      startedAt: null,
      finishedAt: null,
      errorMessage: null,
    },
  };
}

async function installRoutes(page: Page, withCandidates = false): Promise<void> {
  const jobId = JOB_ID.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const step = pipelineStep();

  await page.route('**/api/**', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: '[]',
  }).catch(() => {
    // A more specific route may already have completed this request.
  }));
  await page.route('**/api/auth/status', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      profile: 'local',
      bootstrapRequired: false,
      authenticated: true,
      user: null,
    }),
  }));
  await page.route('**/api/runner/status**', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ projects: {} }),
  }));
  await page.route(/\/api\/cli\/quota(?:\?.*)?$/, route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      at: '2026-08-11T10:00:00Z',
      ttlSeconds: 600,
      snapshots: [],
    }),
  }));
  await page.route('**/api/tasks', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: '[]',
  }));
  await page.route('**/api/tasks/grouped**', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      preparation: [], orchestratorPrep: [], ready: [], progress: [], failedPickup: [],
      autoReview: [], humanReview: [], completed: [], archive: [],
    }),
  }));
  await page.route('**/api/watch-paths**', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify([
      { name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH },
    ]),
  }));
  await page.route('**/api/workspaces**', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: '[]',
  }));
  await page.route('**/api/projects**', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: '[]',
  }));
  await page.route('**/api/projects/*/workbenches**', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ items: [] }),
  }));
  await page.route('**/api/runner/queue-starvation**', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      active: false,
      waitingTaskCount: 0,
      availableSlots: 2,
      thresholdMinutes: 5,
      oldestEnteredLaneAt: null,
      observedAt: '2026-08-11T10:00:00Z',
      items: [],
    }),
  }));
  await page.route('**/api/pipeline/accepted-integration-alert**', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      active: false,
      stalledTaskCount: 0,
      thresholdMinutes: 30,
      oldestAcceptedAt: null,
      observedAt: '2026-08-11T10:00:00Z',
      items: [],
    }),
  }));
  await page.route(new RegExp(`/api/tasks/${jobId}/pipeline(\\?|$)`), route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      pipeline: {
        id: 'task-header-fixture',
        displayName: 'Task header fixture',
        version: 1,
        pre: [step],
        core: [],
        post: [],
        allSteps: [step],
      },
      execution: {
        pipelineId: 'task-header-fixture',
        pipelineVersion: 1,
        jobId: JOB_ID,
        project: PROJECT,
        startedAt: null,
        completedAt: null,
        steps: [],
      },
    }),
  }));
  for (const endpoint of ['output', 'timeline', 'screenshots']) {
    await page.route(new RegExp(`/api/tasks/${jobId}/${endpoint}(\\?|$)`), route => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: '[]',
    }));
  }
  await page.route(new RegExp(`/api/tasks/${jobId}/session-events(\\?|$)`), route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ events: [], sessionChain: [] }),
  }));
  for (const endpoint of ['agent-work', 'claude-session', 'runs']) {
    await page.route(new RegExp(`/api/tasks/${jobId}/${endpoint}(\\?|$)`), route => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: 'null',
    }));
  }
  await page.route(new RegExp(`/api/tasks/${jobId}(\\?|$)`), route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(taskDetail(withCandidates)),
  }));
}

test.describe('Task-header live status', () => {
  test('model picker shows the launch-time benchmark candidate as information', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await installRoutes(page, true);
    await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
    await dismissDevErrorDialog(page);

    const currentRoute = page.getByTestId('chat-compose-model');
    await expect(currentRoute).toContainText('gpt-5.4');
    await currentRoute.click();
    const candidates = page.getByTestId('chat-model-picker-better-candidates');
    await expect(candidates).toBeVisible();
    await expect(candidates).toContainText('gpt-5.6-terra / medium');

    if (RESULTS_DIR) {
      await candidates.screenshot({ path: path.join(RESULTS_DIR, 'agt-2770-model-picker-candidates--mocked.png') });
    }
  });

  test('queued CURRENT and NEXT rows share the banner, tab, and pane grid', async ({ page }) => {
    await page.addInitScript(() => {
      localStorage.setItem(
        'taskboard.panesVisible',
        JSON.stringify({ prompt: true, protocol: false, git: false }),
      );
    });
    await page.setViewportSize({ width: 1440, height: 900 });
    await installRoutes(page);
    await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);

    const taskSurface = page.getByTestId('studio-task');
    const status = taskSurface.getByTestId('task-live-status');
    const banner = page.getByTestId('workspace-banner');
    await expect(banner).toBeAttached();
    await expect(status).toHaveClass(/task-live--detail/);
    await expect(status.getByTestId('task-live-current'))
      .toContainText('Waiting for runner slot · position 3');
    await expect(status.getByTestId('task-live-next')).toContainText('Loop check');
    await expect(status.getByTestId('task-live-detail-inline')).toContainText('Last activity');
    await expect(taskSurface.getByTestId('prompt-tab-overview')).toBeVisible();

    const chrome = await status.evaluate((element) => {
      const style = getComputedStyle(element);
      return {
        backgroundColor: style.backgroundColor,
        borderRadius: style.borderRadius,
        borderLeftWidth: style.borderLeftWidth,
        borderRightWidth: style.borderRightWidth,
      };
    });
    expect(chrome).toEqual({
      backgroundColor: 'rgba(0, 0, 0, 0)',
      borderRadius: '0px',
      borderLeftWidth: '0px',
      borderRightWidth: '0px',
    });

    const bannerEdge = await banner.evaluate((element) => {
      const box = element.getBoundingClientRect();
      return { x: box.x, width: box.width };
    });
    const edges = await Promise.all([
      page.getByTestId('studio-tabbar').boundingBox(),
      status.boundingBox(),
      taskSurface.getByTestId('pane-prompt-header').boundingBox(),
    ]);
    for (const edge of edges) expect(edge).not.toBeNull();
    for (const edge of edges) {
      expect(Math.abs(bannerEdge.x - edge!.x)).toBeLessThanOrEqual(1);
      expect(Math.abs(
        (bannerEdge.x + bannerEdge.width) - (edge!.x + edge!.width),
      )).toBeLessThanOrEqual(1);
    }

    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await expect(page.locator('html')).toHaveAttribute('data-studio-theme', theme);
      await expect(page.getByTestId('error-dialog-overlay')).toHaveCount(0);
      if (RESULTS_DIR) {
        const legacyStyle = await page.addStyleTag({ content: LEGACY_FLOATING_STATUS_CSS });
        const legacyChrome = await status.evaluate((element) => {
          const style = getComputedStyle(element);
          return {
            backgroundColor: style.backgroundColor,
            borderRadius: style.borderRadius,
            borderLeftWidth: style.borderLeftWidth,
            borderRightWidth: style.borderRightWidth,
          };
        });
        expect(legacyChrome.backgroundColor).not.toBe('rgba(0, 0, 0, 0)');
        expect(legacyChrome).toMatchObject({
          borderRadius: '10px',
          borderLeftWidth: '1px',
          borderRightWidth: '1px',
        });
        await page.screenshot({
          path: path.join(RESULTS_DIR, `agt-2637--before--${theme}--mocked.png`),
          fullPage: false,
        });
        await legacyStyle.evaluate(element => element.remove());
        await page.screenshot({
          path: path.join(RESULTS_DIR, `agt-2637--after--${theme}--mocked.png`),
          fullPage: false,
        });
      }
    }
  });
});
