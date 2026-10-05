import { test, expect } from '../fixtures/dev-backend';
import type { Page } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';

const EMPTY_GROUPED = {
  backlog: [], preparation: [], orchestratorPrep: [],
  ready: [], progress: [], failedPickup: [], review: [], autoReview: [],
  humanReview: [], completed: [], archive: [],
};

const resultsDir = process.env.JOB_RESULTS_DIR
  ? path.join(process.env.JOB_RESULTS_DIR, 'empty-state')
  : path.join('test-results', 'empty-state-refresh');

async function openEmptyState(page: Page): Promise<void> {
  await page.route('**/api/**', route => {
    const url = route.request().url();
    const requestPath = decodeURIComponent(new URL(url).pathname);
    const json = (body: unknown) => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(body),
    });
    if (url.includes('/api/v1/studio/auth/status')) {
      return json({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null });
    }
    if (url.includes('/api/runner/queue-starvation')) {
      return json({
        active: false,
        waitingTaskCount: 0,
        availableSlots: 1,
        thresholdMinutes: 15,
        oldestEnteredLaneAt: null,
        observedAt: '2026-08-10T09:00:00Z',
        items: [],
      });
    }
    if (url.includes('/api/pipeline/accepted-integration-alert')) {
      return json({
        active: true,
        stalledTaskCount: 1,
        thresholdMinutes: 30,
        oldestAcceptedAt: '2026-08-10T08:00:00Z',
        observedAt: '2026-08-10T09:00:00Z',
        items: [{
          taskKey: 'AGT-2592',
          taskId: 'welcome-layout',
          projectName: 'Agent Software Studio',
          title: 'Welcome layout',
          acceptedAt: '2026-08-10T08:00:00Z',
          integrationStatus: 'pending',
        }],
      });
    }
    if (url.includes('/api/v1/studio/board')) return json(EMPTY_GROUPED);
    if (url.includes('/api/tasks/archive')) {
      return json({ items: [], total: 0, offset: 0, limit: 50, hasMore: false });
    }
    if (url.includes('/api/v1/studio/runner/status')) return json({ projects: {} });
    if (url.includes('/api/runner/orchestrator-feed')) {
      return json({ entries: [], generatedAtUtc: '2026-08-10T09:00:00Z' });
    }
    if (requestPath === '/api/v1/studio/orchestrator/sessions') return json({ sessions: [] });
    if (requestPath.startsWith('/api/v1/studio/orchestrator/context/')) {
      const contextKey = requestPath.slice('/api/v1/studio/orchestrator/context/'.length);
      return json({
        contextKey,
        capturedAt: '2026-08-10T09:00:00Z',
        digest: `Project context for ${contextKey}`,
        sources: [],
      });
    }
    if (requestPath.startsWith('/api/runner/') && requestPath.endsWith('/orchestrator-chat')) {
      const contextKey = requestPath.slice('/api/runner/'.length, -'/orchestrator-chat'.length);
      return json({ contextKey, project: 'Agent Software Studio', turns: [] });
    }
    if (requestPath === '/api/auto-review/status') {
      return json({
        lastTickAt: null,
        accept: 0,
        reissue: 0,
        escalate: 0,
        aspectsRun: 0,
        pending: 0,
        currentJob: null,
        currentProject: null,
        activeJobs: [],
      });
    }
    if (requestPath === '/api/cli/quota') {
      return json({ at: '2026-08-10T09:00:00Z', snapshots: [], ttlSeconds: 600 });
    }
    if (/^\/api\/cli\/[^/]+\/models$/.test(requestPath)) {
      return json({ models: [], source: 'empty-state-fixture' });
    }
    if (/\/api\/tasks(\?|$)/.test(url)) return json([]);
    if (url.includes('/api/crash-recovery/pending')) return json({ pending: [] });
    if (requestPath === '/api/v1/workspaces' || requestPath === '/api/v1/projects') return json([]);
    if (requestPath === '/api/environment' || requestPath === '/api/projects/settings') return json({});
    if (/^\/api\/clients\/[^/]+\/defaults$/.test(requestPath)) return json({});
    if (requestPath === '/api/tags'
      || requestPath === '/api/clients'
      || requestPath === '/api/clients/'
      || requestPath === '/api/v1/management/remote-hosts') return json([]);
    if (url.includes('/api/watch-paths')) {
      return json([{
        name: 'Agent Software Studio',
        path: '/workspace/agent-software-studio',
        rootPath: '/workspace/agent-software-studio',
        repositoryPath: '/workspace/agent-software-studio',
      }]);
    }
    return route.continue();
  });

  await page.goto('/');
  await expect(page.getByTestId('app-root')).toBeVisible({ timeout: 15_000 });
  let tabCount = await page.locator('.studio-tab__close').count();
  while (tabCount > 0) {
    await page.locator('.studio-tab__close').first().click();
    await expect(page.locator('.studio-tab__close')).toHaveCount(--tabCount);
  }
  await expect(page.getByTestId('studio-welcome')).toBeVisible();
  await expect(page.getByTestId('accepted-integration-alert-banner')).toBeVisible();
}

async function setTheme(page: Page, theme: 'dark' | 'light'): Promise<void> {
  await page.evaluate(selectedTheme => {
    document.documentElement.setAttribute('data-studio-theme', selectedTheme);
  }, theme);
  await expect(page.locator('html')).toHaveAttribute('data-studio-theme', theme);
}

async function openOrchestratorSplit(page: Page): Promise<void> {
  const toggle = page.getByTestId('orch-side-sheet-toggle');
  await expect(toggle).toBeVisible();
  await toggle.click();

  const panel = page.locator('app-orchestrator-side-sheet');
  await expect(panel).toHaveClass(/is-open/);
  await expect.poll(async () => (await panel.boundingBox())?.width ?? 0)
    .toBeGreaterThan(600);
}

async function expectBoundedFlatLayout(
  page: Page,
  graphic: 'visible' | 'hidden',
): Promise<void> {
  const banner = page.getByTestId('accepted-integration-alert-banner');
  const welcome = page.getByTestId('studio-welcome');
  const stage = page.getByTestId('studio-empty-stage');
  const canvas = page.getByTestId('studio-empty-canvas');
  const content = page.getByTestId('studio-welcome-content');

  const [bannerBox, welcomeBox] = await Promise.all([
    banner.boundingBox(),
    welcome.boundingBox(),
  ]);
  expect(bannerBox).not.toBeNull();
  expect(welcomeBox).not.toBeNull();
  expect(welcomeBox!.y).toBeGreaterThanOrEqual(bannerBox!.y + bannerBox!.height + 16);

  const bannerPosition = await banner.evaluate(element => getComputedStyle(element).position);
  expect(bannerPosition).toBe('static');

  const stageStyle = await stage.evaluate(element => {
    const style = getComputedStyle(element);
    return {
      borderTopWidth: style.borderTopWidth,
      display: style.display,
      boxShadow: style.boxShadow,
      overflow: style.overflow,
    };
  });
  expect(stageStyle.borderTopWidth).toBe('0px');
  expect(stageStyle.boxShadow).toBe('none');

  if (graphic === 'visible') {
    expect(stageStyle.display).not.toBe('none');
    expect(stageStyle.overflow).toBe('visible');
    const [stageBox, canvasBox] = await Promise.all([
      stage.boundingBox(),
      canvas.boundingBox(),
    ]);
    expect(stageBox).not.toBeNull();
    expect(canvasBox).not.toBeNull();
    expect(stageBox!.height).toBeLessThanOrEqual(241);
    expect(canvasBox!.x).toBeGreaterThanOrEqual(stageBox!.x - 1);
    expect(canvasBox!.y).toBeGreaterThanOrEqual(stageBox!.y - 1);
    expect(canvasBox!.x + canvasBox!.width).toBeLessThanOrEqual(stageBox!.x + stageBox!.width + 1);
    expect(canvasBox!.y + canvasBox!.height).toBeLessThanOrEqual(stageBox!.y + stageBox!.height + 1);
  } else {
    expect(stageStyle.display).toBe('none');
    await expect(page.getByTestId('studio-empty-subtitle')).toBeVisible();
  }

  const contentStyle = await content.evaluate(element => {
    const style = getComputedStyle(element);
    return {
      borderTopWidth: style.borderTopWidth,
      boxShadow: style.boxShadow,
    };
  });
  expect(contentStyle).toEqual({ borderTopWidth: '0px', boxShadow: 'none' });

  const overflow = await welcome.evaluate(element => ({
    clientWidth: element.clientWidth,
    scrollWidth: element.scrollWidth,
  }));
  expect(overflow.scrollWidth).toBeLessThanOrEqual(overflow.clientWidth + 1);
}

test.describe('studio-shell · refreshed empty state', () => {
  test.setTimeout(45_000);

  test('makes chat primary and captures both themes plus the animation cycle', async ({ page, devBackend }) => {
    expect(devBackend.workspace).toBeTruthy();
    await page.setViewportSize({ width: 1440, height: 900 });
    await openEmptyState(page);
    fs.mkdirSync(resultsDir, { recursive: true });

    const automata = page.getByTestId('studio-empty-state');
    const capture = process.env.EMPTY_STATE_CAPTURE ?? 'after';

    if (capture !== 'before') {
      await expect(page.getByTestId('studio-empty-subtitle'))
        .toHaveText('No tabs open.');
      await expect(page.getByTestId('studio-welcome-chat-hint'))
        .toContainText('Open project chat');
      await expect(page.getByTestId('studio-welcome-open-chat')).toBeVisible();
      await expect(page.getByTestId('studio-welcome-add-task')).toHaveCount(0);
      await expect(page.getByRole('button', { name: 'New task', exact: true })).toHaveCount(0);

      const canvasBox = await page.getByTestId('studio-empty-canvas').boundingBox();
      expect(canvasBox?.width).toBeGreaterThan(500);

      await setTheme(page, 'dark');
      const frames = [
        { phase: 'chaos', minimumProgress: 0 },
        { phase: 'forming', minimumProgress: 0.55 },
        { phase: 'smiley', minimumProgress: 0.9 },
        { phase: 'decay', minimumProgress: 0.45 },
      ];
      for (const [index, { phase, minimumProgress }] of frames.entries()) {
        await expect(automata).toHaveAttribute('data-phase', phase, { timeout: 15_000 });
        if (minimumProgress > 0) {
          await expect.poll(async () => Number(await automata.getAttribute('data-progress')))
            .toBeGreaterThan(minimumProgress);
        }
        await automata.screenshot({
          path: path.join(resultsDir, `cycle-${index + 1}-${phase}--mocked.png`),
        });
      }
    }

    const viewports = [
      { name: 'wide-split', width: 1600, height: 900, graphic: 'visible' },
      { name: 'narrow-split', width: 1280, height: 800, graphic: 'hidden' },
    ] as const;
    await openOrchestratorSplit(page);
    for (const viewport of viewports) {
      await page.setViewportSize({ width: viewport.width, height: viewport.height });
      for (const theme of ['light', 'dark'] as const) {
        await setTheme(page, theme);
        if (capture !== 'before') await expectBoundedFlatLayout(page, viewport.graphic);
        await page.screenshot({
          path: path.join(resultsDir, `${capture}-${theme}-${viewport.name}--mocked.png`),
        });
      }
    }
  });
});
