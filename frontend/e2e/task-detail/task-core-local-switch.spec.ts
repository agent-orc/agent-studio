import { mkdirSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import type { Page } from '@playwright/test';
import { test, expect } from '../fixtures/dev-backend';
import { api } from '../helpers/api';
import { createJob } from '../helpers/jobs';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

/**
 * Local end-to-end evidence for the progressive task switch (AGT-2955).
 *
 * Unlike `task-detail-instant-navigation.spec.ts`, nothing here is mocked:
 * the dev frontend proxies to the worktree's own backend started by the
 * `dev-backend` fixture, so every number includes the real transport, the
 * real `/core` and `/details/*` handlers, and the browser render. The same
 * run times the legacy full-detail route for the same tasks, which is the
 * wait a core-first switch no longer includes. Results land in
 * `JOB_RESULTS_DIR` when it is set; the budget assertion always runs.
 */

interface WatchPath { path: string }

const LARGE_PROMPT = `# Local core switch fixture\n\n${'Large markdown paragraph with `code` and **emphasis**. '.repeat(4_000)}`;

function percentile(sorted: readonly number[], p: number): number {
  return Math.round(sorted[Math.max(0, Math.ceil(sorted.length * p) - 1)] * 10) / 10;
}

function summary(samples: readonly number[]) {
  const sorted = [...samples].sort((a, b) => a - b);
  return { samples: sorted.length, p50Ms: percentile(sorted, 0.5), p95Ms: percentile(sorted, 0.95),
    maxMs: Math.round(sorted[sorted.length - 1] * 10) / 10 };
}

async function coreReadyCount(page: Page): Promise<number> {
  return page.evaluate(() => performance.getEntriesByName('task-core-ready').length);
}

test('measures local end-to-end core switches against the legacy detail wait', async ({ page, devBackend }, testInfo) => {
  void devBackend;
  testInfo.setTimeout(300_000);
  const watchPath = (await api<WatchPath[]>('/api/watch-paths'))[0]?.path;
  if (!watchPath) throw new Error('The dev backend exposes no watch path');
  const stamp = `${Date.now()}-${Math.floor(Math.random() * 10_000)}`;
  const ids = [1, 2].map(index => `e2e-core-switch-${stamp}-${index}`);
  for (const id of ids) {
    await createJob({ id, title: id, watchPath, targetState: '5-human-review',
      promptMarkdown: LARGE_PROMPT, fixture: false });
  }

  let releaseDocuments: (() => void) | null = null;
  try {
    await page.route('**/api/crash-recovery/pending', route => route.fulfill({
      status: 200, contentType: 'application/json', body: JSON.stringify({ pending: [] }),
    }));
    let project: string | null = null;
    page.on('request', request => {
      const url = new URL(request.url());
      if (url.pathname.endsWith('/core')) project ??= url.searchParams.get('project');
    });
    // Any refused task read would evict the selection; record them so a
    // failure names the route instead of only the resulting UI state.
    const refusedTaskReads: string[] = [];
    page.on('response', response => {
      const url = new URL(response.url());
      if (url.pathname.startsWith('/api/tasks/') && response.status() >= 400)
        refusedTaskReads.push(`${response.status()} ${url.pathname}${url.search}`);
    });
    await page.goto('/');
    await dismissDevErrorDialog(page);
    for (const id of ids)
      await expect(page.getByTestId('task-card').filter({ hasText: id }).first()).toBeVisible({ timeout: 30_000 });

    // Cold open: board click to a painted core, real transport end to end.
    await page.evaluate(() => document.addEventListener('click',
      () => performance.mark('local-open-click'), { capture: true, once: true }));
    const card = page.getByTestId('task-card').filter({ hasText: ids[0] }).first();
    const box = await card.boundingBox();
    if (!box) throw new Error('Task card has no layout box');
    await card.click({ position: { x: box.width / 2, y: box.height - 4 }, force: true });
    await page.waitForFunction(() => performance.getEntriesByName('task-core-ready').length > 0);
    const coldOpenMs = await page.evaluate(() => {
      const click = performance.getEntriesByName('local-open-click').at(-1)!;
      const ready = performance.getEntriesByName('task-core-ready').at(-1)!;
      return ready.startTime - click.startTime;
    });
    await expect(page.getByTestId('studio-task')).toContainText(ids[0]);

    // Thirty keyboard switches between the two tasks. Each waits for the
    // rich pane, so every step is a real switch away from a loaded task.
    const switches: number[] = [];
    for (let index = 0; index < 30; index++) {
      const before = await coreReadyCount(page);
      await page.evaluate(() => document.addEventListener('keydown',
        () => performance.mark('local-switch-start'), { capture: true, once: true }));
      await page.keyboard.press(index % 2 === 0 ? 'j' : 'k');
      await page.waitForFunction(count => performance.getEntriesByName('task-core-ready').length > count, before);
      switches.push(await page.evaluate(() => {
        const start = performance.getEntriesByName('local-switch-start').at(-1)!;
        const ready = performance.getEntriesByName('task-core-ready').at(-1)!;
        return ready.startTime - start.startTime;
      }));
      await expect(page.getByTestId('task-core'), refusedTaskReads.join('\n')).toHaveCount(0, { timeout: 15_000 });
    }
    expect(refusedTaskReads, 'no task read may be refused for a live task').toEqual([]);

    // Same tasks, same backend, same proxy: the bounded core read against
    // the legacy full-detail read that the old switch waited for.
    expect(project, 'the app must have issued at least one /core request').toBeTruthy();
    const reads = await page.evaluate(async ({ taskIds, handle }) => {
      const time = async (url: string) => {
        const started = performance.now();
        const response = await fetch(url, { cache: 'no-store' });
        await response.arrayBuffer();
        if (!response.ok) throw new Error(`${url} -> ${response.status}`);
        return performance.now() - started;
      };
      const core: number[] = [];
      const legacy: number[] = [];
      for (let index = 0; index < 30; index++) {
        const id = encodeURIComponent(taskIds[index % 2]);
        core.push(await time(`/api/tasks/${id}/core?project=${encodeURIComponent(handle!)}`));
        legacy.push(await time(`/api/tasks/${id}?project=${encodeURIComponent(handle!)}`));
      }
      return { core, legacy };
    }, { taskIds: ids, handle: project });

    // Genuine screenshots of the real task in both themes: first the core
    // view while documents are held, then the rich pane.
    const documentGate = new Promise<void>(resolve => { releaseDocuments = resolve; });
    await page.route('**/api/tasks/*/details/documents**', async route => {
      await documentGate;
      await route.continue().catch(() => undefined);
    });
    await page.keyboard.press('j');
    await expect(page.getByTestId('task-core')).toBeVisible();
    for (const id of ['identity', 'state', 'pins', 'execution', 'status', 'prompt', 'timeline'])
      await expect(page.getByTestId(`task-core-${id}`)).toBeVisible();

    const resultsDir = process.env.JOB_RESULTS_DIR;
    if (resultsDir) mkdirSync(resultsDir, { recursive: true });
    const shot = async (name: string) => {
      if (resultsDir) await page.screenshot({ path: path.join(resultsDir, name), fullPage: false });
    };
    await setTheme(page, 'light');
    await shot('task-core-local-core-light.png');
    await setTheme(page, 'dark');
    await shot('task-core-local-core-dark.png');
    releaseDocuments!();
    await expect(page.getByTestId('task-core')).toHaveCount(0, { timeout: 15_000 });
    await expect(page.getByTestId('studio-task')).toBeVisible();
    await shot('task-core-local-rich-dark.png');
    await setTheme(page, 'light');
    await shot('task-core-local-rich-light.png');

    const cachedSwitch = summary(switches);
    const coreRead = summary(reads.core);
    const legacyRead = summary(reads.legacy);
    const report = {
      environment: 'local Linux workstation, headless Chromium, ng serve proxy to the worktree dev backend on :5030; real transport, no API mocks',
      fixture: 'two human-review tasks with a 220 KB prompt in the fixture workspace; not the production-shaped snapshot of the dossier baseline',
      coldOpenMs: Math.round(coldOpenMs * 10) / 10,
      switchToCoreReady: cachedSwitch,
      coreRead,
      legacyDetailRead: legacyRead,
      excludedLegacyWait: {
        note: 'Measured legacy full-detail read on this backend. A core-first switch paints before any of it; the difference is measured, not inferred from the dossier.',
        p50Ms: legacyRead.p50Ms, p95Ms: legacyRead.p95Ms,
        p50SavingVsCoreReadyMs: Math.round((legacyRead.p50Ms - cachedSwitch.p50Ms) * 10) / 10,
        p95SavingVsCoreReadyMs: Math.round((legacyRead.p95Ms - cachedSwitch.p95Ms) * 10) / 10,
      },
      budgetMs: 100,
    };
    if (resultsDir)
      writeFileSync(path.join(resultsDir, 'task-core-local-e2e.json'), `${JSON.stringify(report, null, 2)}\n`);
    console.log(JSON.stringify(report, null, 2));
    expect(cachedSwitch.p95Ms, 'local end-to-end switch to core-ready p95').toBeLessThanOrEqual(100);
  } finally {
    releaseDocuments?.();
    for (const id of ids) {
      await api(`/api/tasks/${encodeURIComponent(id)}?watchPath=${encodeURIComponent(watchPath)}`, { method: 'DELETE' })
        .catch(() => undefined);
    }
  }
});
