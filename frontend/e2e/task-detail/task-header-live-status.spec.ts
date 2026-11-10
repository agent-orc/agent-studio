import { expect, Page, test } from '@playwright/test';
import * as path from 'path';
import { setTheme } from '../helpers/theme';

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

function taskDetail() {
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

async function installRoutes(page: Page, detail = taskDetail()): Promise<void> {
  const jobId = JOB_ID.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const step = pipelineStep();

  await page.route('**/api/**', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: '[]',
  }).catch(() => {
    // A more specific route may already have completed this request.
  }));
  await page.route('**/api/v1/studio/auth/status', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      profile: 'local',
      bootstrapRequired: false,
      authenticated: true,
      user: null,
    }),
  }));
  await page.route('**/api/usage/cockpit**', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ clis: [], cost: null }),
  }));
  await page.route('**/api/v1/studio/runner/status**', route => route.fulfill({
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
  await page.route(new RegExp(`/api/tasks/${jobId}/session-events(\\?|$)`), route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ events: [], sessionChain: [], currentSessionId: null }),
  }));
  await page.route(new RegExp(`/api/tasks/${jobId}/runs(\\?|$)`), route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ runs: [] }),
  }));
  await page.route('**/api/v1/studio/board**', route => route.fulfill({
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
  await page.route(/\/api\/(?:workspaces|v1\/workspaces(?:\?|$))/, route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: '[]',
  }));
  await page.route(/\/api\/(?:projects|v1\/projects(?:\?|$))/, route => route.fulfill({
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
  await page.route(new RegExp(`/api/v1/projects/[^/]+/tasks/${jobId}(\\?|$)`), route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(detail),
  }));
}

test.describe('Task-header live status', () => {
  test('Manual remote Run now explains the claim gate and switches pickup inline', async ({ page }) => {
    const detail = taskDetail();
    const remoteDetail = {
      ...detail,
      info: {
        ...detail.info,
        liveStatus: {
          ...detail.info.liveStatus,
          queue: { kind: 'runner', position: 1, reasonCode: 'manual-mode',
            reason: 'Auto-pickup is off; switch the project to auto to let its runner claim the card.' },
        },
        executionLocation: {
          state: 'no-active-execution', executionKind: 'none',
          configuredRunnerId: 'first-run-coding', hostDisplayName: 'first-run-coding',
        },
      },
    };
    let startCalls = 0;
    let modeCalls = 0;
    await installRoutes(page, remoteDetail);
    await page.route(new RegExp(`/api/v1/projects/[^/]+/tasks/${JOB_ID}/start(\\?|$)`), route => {
      startCalls++;
      return route.fulfill({ status: 202, contentType: 'application/json',
        body: JSON.stringify({ status: 'queued', queued: { reason: 'remote-execution' } }) });
    });
    await page.route(`**/api/runner/${PROJECT}/mode`, route => {
      modeCalls++;
      expect(route.request().postDataJSON()).toEqual({ mode: 'auto-continuous' });
      return route.fulfill({ status: 200, contentType: 'application/json', body: '{}' });
    });
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);

    await page.getByTestId('studio-triage-action-run-now').click();
    await expect.poll(() => startCalls).toBe(1);
    const status = page.getByTestId('studio-task').getByTestId('task-live-status');
    await expect(status).toHaveAttribute('data-wait-reason', 'manual-mode');
    await expect(status.getByTestId('task-live-current'))
      .toContainText('Pickup is Manual: switch to Auto to let first-run-coding claim it');
    await expect(status.getByTestId('task-live-current')).not.toContainText('Waiting for runner slot');

    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.screenshot({ path: path.join(RESULTS_DIR, `agt-3619-manual-remote-${theme}.png`) });
    }
    await page.setViewportSize({ width: 390, height: 844 });
    await expect(status.getByTestId('task-live-switch-auto')).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(390);
    await page.screenshot({ path: path.join(RESULTS_DIR, 'agt-3619-manual-remote-390px.png') });

    await status.getByTestId('task-live-switch-auto').click();
    await expect.poll(() => modeCalls).toBe(1);
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
      .toContainText('Queued for runner · position 3');
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
