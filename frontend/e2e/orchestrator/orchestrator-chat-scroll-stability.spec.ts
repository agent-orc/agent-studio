import { expect, test, type Page } from '@playwright/test';
import { writeFile } from 'node:fs/promises';
import { installFrontendOverride } from '../helpers/frontend-override';
import { setTheme } from '../helpers/theme';

/**
 * Regression for the live Orchestrator transcript's 4px scrollbar jitter.
 *
 * The transport projection already tracks every event by its durable turn id.
 * The unstable class was the mixed-height message row at the virtual-window
 * boundary: short turns, wrapped Markdown, and rows with metadata were all
 * represented by the same 120px spacer estimate. Every appended turn removed
 * a real DOM row, changed the spacer by 120px, and made sticky-to-bottom issue
 * a corrective scroll after layout. The result was a moving scrollbar thumb
 * and selective row flicker even though most tracked rows stayed mounted.
 *
 * This spec accelerates the normal 30-second silent transcript poll, appends
 * deterministic mixed-height turns, and observes the real DOM while the live
 * tail advances. The Orchestrator host must remain the sole scroll container,
 * must not create virtual spacers, and must not remove existing tracked rows.
 */

interface StubTurn {
  id: string;
  ts: string;
  role: 'user' | 'orchestrator';
  text: string;
}

interface ScrollProbe {
  removedRowIds: string[];
  removedTaskPillRowIds: string[];
  initialTaskPills: { rowId: string; node: Element }[];
  taskPillMountIds: string[];
  samples: {
    reason: 'initial' | 'mutation' | 'resize' | 'scroll';
    top: number;
    height: number;
    topSpacerHeight: number;
  }[];
}

const INITIAL_TURNS = 72;
const FINAL_TURNS = 80;
const PROJECT = 'scroll-stability';
const TASK_KEYS = ['AGT-2235', 'AGT-2236', 'AGT-2237', 'AGT-2238'];

interface TranscriptController {
  startGrowth(): void;
  visibleCount(): number;
  growthPollCount(): number;
}

function buildTurns(): StubTurn[] {
  const base = Date.parse('2026-07-22T10:00:00Z');
  return Array.from({ length: FINAL_TURNS }, (_, index) => ({
    id: `jitter-turn-${index}`,
    ts: new Date(base + index * 1_000).toISOString(),
    role: index % 2 === 0 ? 'user' : 'orchestrator',
    text: index % 4 === 1
      ? `Detailed streamed turn ${index}: ${TASK_KEYS.join(' ')}\n\n`
        + '- first measured line\n- second measured line\n- task-pill-sized tail'
      : `Short streamed turn ${index}`,
  }));
}

async function stubGrowingTranscript(page: Page): Promise<TranscriptController> {
  const turns = buildTurns();
  let visibleCount = INITIAL_TURNS;
  let growthStarted = false;
  let growthPollCount = 0;

  await installFrontendOverride(page);

  // Keep the rendering regression independent of the operator's auth profile
  // and current board. More specific route handlers registered below win over
  // this shell fixture.
  await page.route(/\/api\//, route => {
    const requestPath = new URL(route.request().url()).pathname;
    let body = '{}';
    if (requestPath === '/api/v1/studio/auth/status') {
      body = JSON.stringify({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null });
    } else if (requestPath === '/api/watch-paths') {
      body = JSON.stringify([{ name: PROJECT, path: `/tmp/${PROJECT}`, rootPath: `/tmp/${PROJECT}` }]);
    } else if (requestPath === '/api/v1/workspaces') {
      body = JSON.stringify([{
        id: 'workspace-1', displayName: 'Scroll fixture', sortOrder: 0, isDefault: true,
        projects: [{ id: PROJECT, displayName: PROJECT, shortCode: 'SS', workspaceId: 'workspace-1',
          storageLocation: `/tmp/${PROJECT}`, archived: false, urls: [] }],
      }]);
    } else if (requestPath === '/api/tasks/archive') {
      body = JSON.stringify({ items: [], total: 0, offset: 0, limit: 50 });
    } else if (requestPath === '/api/tasks') {
      body = '[]';
    } else if (requestPath === '/api/v1/studio/board') {
      body = JSON.stringify({ backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
        failedPickup: [], autoReview: [], humanReview: [], review: [], completed: [], archive: [] });
    } else if (requestPath === '/api/v1/studio/orchestrator/sessions') {
      body = JSON.stringify({ sessions: [] });
    } else if (/\/api\/(?:tags|clients|workspaces\/[^/]+\/tags)\/?$/.test(requestPath)) {
      body = '[]';
    } else if (requestPath === '/api/v1/studio/runner/status') {
      body = JSON.stringify({ projects: {} });
    } else if (requestPath === '/api/cli/quota') {
      body = JSON.stringify({ snapshots: [] });
    } else if (/\/api\/cli\/(?:codex|claude|gemini)\/models$/.test(requestPath)) {
      body = JSON.stringify({ models: [], source: 'scroll-fixture' });
    } else if (requestPath.startsWith('/api/bus/')) {
      body = '[]';
    }
    return route.fulfill({ status: 200, contentType: 'application/json', body });
  });

  await page.route('**/api/tasks/reference-status', route => route.fulfill({
    contentType: 'application/json',
    body: JSON.stringify({
      items: TASK_KEYS.map(key => ({
        key,
        exists: true,
        taskKey: `${PROJECT}::${key}`,
        title: `Task reference ${key}`,
        projectId: PROJECT,
        projectName: PROJECT,
        projectColor: '#7c6fdd',
        lane: '3-progress',
        merge: null,
        reviewGrade: null,
      })),
    }),
  }));

  // Accelerate only the side sheet's documented 30-second silent poll. Other
  // application timers keep their production cadence.
  await page.addInitScript(() => {
    const nativeSetInterval = window.setInterval.bind(window);
    window.setInterval = ((handler: TimerHandler, timeout?: number, ...args: unknown[]) =>
      nativeSetInterval(handler, timeout === 30_000 ? 80 : timeout, ...args)) as typeof window.setInterval;
  });

  await page.route(/\/api\/runner\/[^/]+(?:\/[^/]+)?\/orchestrator-chat$/, async route => {
    if (route.request().method() !== 'GET') {
      await route.fallback();
      return;
    }
    if (growthStarted) {
      growthPollCount += 1;
      visibleCount = Math.min(FINAL_TURNS, visibleCount + 1);
    }
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ project: PROJECT, turns: turns.slice(0, visibleCount) }),
    });
  });

  return {
    startGrowth: () => { growthStarted = true; },
    visibleCount: () => visibleCount,
    growthPollCount: () => growthPollCount,
  };
}

async function openChat(page: Page) {
  await page.goto('/', { waitUntil: 'domcontentloaded' });
  await page.getByTestId('orch-side-sheet-toggle').click();
  const conversation = page.getByTestId('orchestrator-conversation');
  await expect(conversation).toBeVisible();
  await expect(page.getByText('Short streamed turn 70')).toBeVisible();
  return conversation;
}

async function installProbe(page: Page): Promise<void> {
  await page.getByTestId('orchestrator-conversation').evaluate(host => {
    const view = host.querySelector<HTMLElement>('[data-testid="conversation-view"]');
    const activeScroller = [host as HTMLElement, view]
      .find(candidate => candidate && /auto|scroll/.test(getComputedStyle(candidate).overflowY))
      ?? host as HTMLElement;
    const initialTaskPills = Array.from(
      host.querySelectorAll('[data-testid="task-reference-microcard"]'),
      node => ({
        rowId: node.closest<HTMLElement>('[data-item-id]')?.dataset['itemId'] ?? 'unknown',
        node,
      }),
    );
    const taskPillId = (node: Element): string => {
      const rowId = node.closest<HTMLElement>('[data-item-id]')?.dataset['itemId'] ?? 'unknown';
      const key = (node as HTMLElement).dataset['taskReferenceKey'] ?? node.textContent?.trim() ?? 'unknown';
      return `${rowId}:${key}`;
    };
    const probe: ScrollProbe = {
      removedRowIds: [],
      removedTaskPillRowIds: [],
      initialTaskPills,
      taskPillMountIds: initialTaskPills.map(pill => taskPillId(pill.node)),
      samples: [],
    };
    (window as typeof window & { __orchestratorScrollProbe?: ScrollProbe })
      .__orchestratorScrollProbe = probe;

    const rowId = (node: Node): string | null => {
      if (!(node instanceof HTMLElement)) return null;
      const item = node.matches('[data-item-id]')
        ? node
        : node.querySelector<HTMLElement>('[data-item-id]');
      return item?.dataset['itemId'] ?? null;
    };
    const containsTaskPill = (node: Node): boolean =>
      node instanceof HTMLElement
      && (node.matches('[data-testid="task-reference-microcard"]')
        || !!node.querySelector('[data-testid="task-reference-microcard"]'));
    const taskPillsIn = (node: Node): Element[] => {
      if (!(node instanceof HTMLElement)) return [];
      const nested = Array.from(node.querySelectorAll('[data-testid="task-reference-microcard"]'));
      return node.matches('[data-testid="task-reference-microcard"]') ? [node, ...nested] : nested;
    };
    const sample = (reason: ScrollProbe['samples'][number]['reason']) => {
      const topSpacer = host
        .querySelector<HTMLElement>('[data-testid="conversation-spacer-top"]');
      probe.samples.push({
        reason,
        top: activeScroller.scrollTop,
        height: activeScroller.scrollHeight,
        topSpacerHeight: topSpacer?.getBoundingClientRect().height ?? 0,
      });
    };
    new MutationObserver(records => {
      for (const record of records) {
        for (const node of record.removedNodes) {
          const id = rowId(node);
          if (!id) continue;
          probe.removedRowIds.push(id);
          if (containsTaskPill(node)) probe.removedTaskPillRowIds.push(id);
        }
        for (const node of record.addedNodes) {
          probe.taskPillMountIds.push(...taskPillsIn(node).map(taskPillId));
        }
      }
      queueMicrotask(() => sample('mutation'));
    }).observe(host, { childList: true, subtree: true });

    const resizeObserver = new ResizeObserver(() => sample('resize'));
    resizeObserver.observe(activeScroller);
    const feed = host.querySelector<HTMLElement>('[data-testid="conversation-feed"]');
    if (feed) resizeObserver.observe(feed);
    activeScroller.addEventListener('scroll', () => sample('scroll'), { passive: true });
    sample('initial');
  });
}

test('mixed-height streamed rows stay mounted without scrollbar correction', async ({ page }, testInfo) => {
  const evidenceLabel = (process.env['PW_EVIDENCE_LABEL'] ?? 'after')
    .replace(/[^a-z0-9-]/gi, '-')
    .toLowerCase();
  const transcript = await stubGrowingTranscript(page);
  const conversation = await openChat(page);
  await installProbe(page);

  transcript.startGrowth();
  await expect.poll(transcript.visibleCount, { message: 'accelerated transcript growth' })
    .toBe(FINAL_TURNS);
  await expect.poll(transcript.growthPollCount, {
    message: 'three unchanged heartbeat polls after the last appended turn',
  }).toBeGreaterThanOrEqual(FINAL_TURNS - INITIAL_TURNS + 3);
  await expect(page.getByText('Detailed streamed turn 77')).toBeVisible();
  await expect.poll(() => page.evaluate(() =>
    (window as typeof window & { __orchestratorScrollProbe?: ScrollProbe })
      .__orchestratorScrollProbe?.taskPillMountIds.length ?? 0), {
    message: 'task-pill hydrator mounted at least one reference',
  }).toBeGreaterThan(0);

  const snapshot = await conversation.evaluate(host => {
    const probe = (window as typeof window & { __orchestratorScrollProbe?: ScrollProbe })
      .__orchestratorScrollProbe
      ?? {
        removedRowIds: [],
        removedTaskPillRowIds: [],
        initialTaskPills: [],
        taskPillMountIds: [],
        samples: [],
      };
    const style = getComputedStyle(host);
    const view = host.querySelector<HTMLElement>('[data-testid="conversation-view"]');
    const activeScroller = [host as HTMLElement, view]
      .find(candidate => candidate && /auto|scroll/.test(getComputedStyle(candidate).overflowY))
      ?? host as HTMLElement;
    const backwards = probe.samples.slice(1).map((sample, index) =>
      probe.samples[index].top - sample.top);
    const spacerDeltas = probe.samples.slice(1).map((sample, index) =>
      Math.abs(sample.topSpacerHeight - probe.samples[index].topSpacerHeight));
    const taskPillMountCounts = new Map<string, number>();
    for (const id of probe.taskPillMountIds) {
      taskPillMountCounts.set(id, (taskPillMountCounts.get(id) ?? 0) + 1);
    }
    return {
      overflowY: style.overflowY,
      conversationOverflowY: view ? getComputedStyle(view).overflowY : null,
      activeScroller: activeScroller === host ? 'orchestrator-host' : 'conversation-view',
      topSpacers: host.querySelectorAll('[data-testid="conversation-spacer-top"]').length,
      bottomSpacers: host.querySelectorAll('[data-testid="conversation-spacer-bottom"]').length,
      taskPillCount: host.querySelectorAll('[data-testid="task-reference-microcard"]').length,
      removedRowIds: probe.removedRowIds,
      removedTaskPillRowIds: probe.removedTaskPillRowIds,
      duplicateTaskPillMountIds: Array.from(taskPillMountCounts)
        .filter(([, count]) => count > 1)
        .map(([id]) => id),
      disconnectedInitialTaskPillRows: probe.initialTaskPills
        .filter(pill => !pill.node.isConnected)
        .map(pill => pill.rowId),
      maxBackwardScroll: Math.max(0, ...backwards),
      maxTopSpacerDelta: Math.max(0, ...spacerDeltas),
      distanceFromBottom:
        activeScroller.scrollHeight - activeScroller.scrollTop - activeScroller.clientHeight,
      samples: probe.samples,
    };
  });

  const probeName = `scroll-probe-${evidenceLabel}.json`;
  const probePath = testInfo.outputPath(probeName);
  await writeFile(probePath, JSON.stringify(snapshot, null, 2), 'utf8');
  await testInfo.attach(probeName, {
    path: probePath,
    contentType: 'application/json',
  });

  expect(snapshot.topSpacers).toBe(0);
  expect(snapshot.bottomSpacers).toBe(0);
  expect(snapshot.overflowY).toBe('auto');
  expect(snapshot.conversationOverflowY).not.toMatch(/auto|scroll/);
  expect(snapshot.removedRowIds).toEqual([]);
  expect(snapshot.removedTaskPillRowIds).toEqual([]);
  expect(snapshot.duplicateTaskPillMountIds).toEqual([]);
  expect(snapshot.disconnectedInitialTaskPillRows).toEqual([]);
  expect(snapshot.taskPillCount).toBeGreaterThan(0);
  expect(snapshot.maxTopSpacerDelta).toBe(0);
  expect(snapshot.maxBackwardScroll).toBeLessThanOrEqual(1);
  expect(snapshot.distanceFromBottom).toBeLessThanOrEqual(24);

  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    const screenshotName = `orchestrator-stream-${evidenceLabel}-${theme}.png`;
    const screenshotPath = testInfo.outputPath(screenshotName);
    await page.screenshot({ path: screenshotPath });
    await testInfo.attach(screenshotName, {
      path: screenshotPath,
      contentType: 'image/png',
    });
  }
});
