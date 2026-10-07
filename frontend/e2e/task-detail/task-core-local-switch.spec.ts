import { mkdirSync, writeFileSync } from 'node:fs';
import { loadavg, cpus } from 'node:os';
import path from 'node:path';
import type { Page } from '@playwright/test';
import { test, expect } from '../fixtures/dev-backend';
import { api } from '../helpers/api';
import { createJob } from '../helpers/jobs';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

// Include the fixture's cold backend startup in this test's timeout.
test.describe.configure({ timeout: 900_000 });

/**
 * Local end-to-end evidence for the progressive task switch (AGT-2955).
 *
 * Unlike `task-detail-instant-navigation.spec.ts`, task API reads are real:
 * the dev frontend proxies to the worktree's own backend started by the
 * `dev-backend` fixture, so every number includes the real transport, the
 * real `/core` and `/details/*` handlers, and the browser render. The same
 * run times the legacy full-detail route for the same tasks, which is the
 * wait a core-first switch no longer includes. Results land in
 * `JOB_RESULTS_DIR` when it is set. The 100 ms budget is asserted only on a
 * controlled workstation run; shared CI hosts still publish the raw result.
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

/** Step toward the other task from the rich view's pager position, never off the lane end. */
async function stepToOtherTask(page: Page): Promise<string> {
  const position = (await page.getByTestId('studio-task-pager-position').innerText()).trim();
  return position.startsWith('1 ') ? 'j' : 'k';
}

test('measures local end-to-end core switches against the legacy detail wait', async ({ page, devBackend }) => {
  const watchPath = (await api<WatchPath[]>('/api/watch-paths'))[0]?.path;
  if (!watchPath) throw new Error('The dev backend exposes no watch path');
  const stamp = `${Date.now()}-${Math.floor(Math.random() * 10_000)}`;
  const ids = [1, 2].map(index => `e2e-core-switch-${stamp}-${index}`);
  for (const id of ids) {
    await createJob({ id, title: id, watchPath, targetState: '5-human-review',
      promptMarkdown: LARGE_PROMPT, requiresIntegration: false, fixture: false });
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
    // The real backend may keep unrelated startup reads open. The task board
    // assertions below establish readiness without waiting for every load.
    await page.goto('/', { waitUntil: 'domcontentloaded', timeout: 60_000 });
    await dismissDevErrorDialog(page);
    for (const id of ids)
      await expect(page.getByTestId('task-card').filter({ hasText: id }).first()).toBeVisible({ timeout: 30_000 });

    // Cold open: board click to a painted core, real transport end to end.
    await page.evaluate(() => document.addEventListener('click',
      () => performance.mark('local-open-click'), { capture: true, once: true }));
    const card = page.getByTestId('task-card').filter({ hasText: ids[0] }).first();
    await card.click();
    // The first open also downloads the lazy task view. In a production
    // build that chunk can land after the documents, so the first view may
    // already be the rich pane; record which one painted first.
    const firstView = await page.waitForFunction(() => {
      if (performance.getEntriesByName('task-core-ready').length > 0) return 'core';
      if (document.querySelector('[data-testid="studio-task"] app-job-detail')) {
        performance.mark('local-open-rich');
        return 'rich';
      }
      return null;
    }, undefined, { polling: 'raf', timeout: 30_000 }).then(handle => handle.jsonValue());
    const coldOpenMs = await page.evaluate(view => {
      const click = performance.getEntriesByName('local-open-click').at(-1)!;
      const ready = performance.getEntriesByName(view === 'core' ? 'task-core-ready' : 'local-open-rich').at(-1)!;
      return ready.startTime - click.startTime;
    }, firstView);
    await expect(page.getByTestId('studio-task')).toContainText(ids[0]);

    // Thirty keyboard switches between the two tasks. Each waits for the
    // rich pane, so every step is a real switch away from a loaded task.
    const switches: number[] = [];
    const richSwitches: number[] = [];
    for (let index = 0; index < 30; index++) {
      const before = await coreReadyCount(page);
      const beforeRich = await page.evaluate(() => performance.getEntriesByName('local-switch-rich').length);
      const hash = await page.evaluate(() => location.hash);
      const key = await stepToOtherTask(page);
      const target = ids[(index + 1) % 2];
      // Observe the rich pane from the same keydown as core-ready. Mark the
      // next animation frame after its DOM appears, so the paired delta
      // measures the local gain from progressive rendering.
      await page.evaluate(({ previous, nextTitle }) => document.addEventListener('keydown', () => {
        const watch = () => {
          const rich = document.querySelector('[data-testid="studio-task"] app-job-detail');
          if (location.hash !== previous && rich?.textContent?.includes(nextTitle)) {
            requestAnimationFrame(() => performance.mark('local-switch-rich'));
          } else requestAnimationFrame(watch);
        };
        requestAnimationFrame(watch);
      }, { capture: true, once: true }), { previous: hash, nextTitle: target });
      await page.evaluate(() => document.addEventListener('keydown',
        () => performance.mark('local-switch-start'), { capture: true, once: true }));
      await page.keyboard.press(key);
      // A core-ready mark counts only once the route names the other task.
      await page.waitForFunction(({ count, previous }) => location.hash !== previous
        && performance.getEntriesByName('task-core-ready').length > count, { count: before, previous: hash });
      switches.push(await page.evaluate(() => {
        const start = performance.getEntriesByName('local-switch-start').at(-1)!;
        const ready = performance.getEntriesByName('task-core-ready').at(-1)!;
        return ready.startTime - start.startTime;
      }));
      await page.waitForFunction(count => performance.getEntriesByName('local-switch-rich').length > count,
        beforeRich, { polling: 'raf', timeout: 15_000 });
      richSwitches.push(await page.evaluate(() => {
        const start = performance.getEntriesByName('local-switch-start').at(-1)!;
        const ready = performance.getEntriesByName('local-switch-rich').at(-1)!;
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

    const cachedSwitch = summary(switches);
    const richSwitch = summary(richSwitches);
    const observedCoreLead = summary(richSwitches.map((rich, index) => rich - switches[index]));
    const coreRead = summary(reads.core);
    const legacyRead = summary(reads.legacy);
    const report = {
      environment: `shared Linux runner, headless Chromium, ng serve proxy to the worktree dev backend on :${devBackend.port}; real task API transport`,
      hostLoad: { cpus: cpus().length, loadAverage1m: Math.round(loadavg()[0] * 10) / 10 },
      fixture: 'two human-review tasks with a 220 KB prompt in the fixture workspace; not the production-shaped snapshot of the dossier baseline',
      coldOpenMs: Math.round(coldOpenMs * 10) / 10,
      coldOpenFirstView: firstView,
      switchToCoreReady: cachedSwitch,
      switchToRichReady: richSwitch,
      observedCoreLead,
      coreRead,
      legacyDetailRead: legacyRead,
      comparisonScope: 'Observed core lead pairs two paint milestones of the same real keyboard switches and is the local end-to-end gain from progressive rendering. Legacy values are separate same-backend endpoint reads, not old UI switches, so their percentiles are not subtracted from core-ready.',
      budgetMs: 100,
      budgetMetOnThisHost: cachedSwitch.p95Ms <= 100,
    };
    const resultsDir = process.env.JOB_RESULTS_DIR;
    if (resultsDir) {
      mkdirSync(resultsDir, { recursive: true });
      writeFileSync(path.join(resultsDir, 'task-core-local-e2e.json'), `${JSON.stringify(report, null, 2)}\n`);
    }
    console.log(JSON.stringify(report, null, 2));

    // Capture the real rich task before holding the next document read. The
    // timed measurements are already durable if that later navigation fails.
    const shot = async (name: string) => {
      if (resultsDir) await page.screenshot({ path: path.join(resultsDir, name), fullPage: false });
    };
    await setTheme(page, 'dark');
    await shot('task-core-local-rich-dark.png');
    await setTheme(page, 'light');
    await shot('task-core-local-rich-light.png');

    // Genuine core screenshots in both themes while the next documents wait.
    const documentGate = new Promise<void>(resolve => { releaseDocuments = resolve; });
    await page.route('**/api/tasks/*/details/documents**', async route => {
      await documentGate;
      await route.continue().catch(() => undefined);
    });
    // Keyboard switching is measured above. Browser Back returns to the
    // previous task through the same core-first restore, independent of the
    // lane order the background reads may have refreshed.
    const shownHash = await page.evaluate(() => location.hash);
    await page.goBack();
    await expect.poll(() => page.evaluate(() => location.hash)).not.toBe(shownHash);
    await expect(page.getByTestId('task-core')).toBeVisible();
    for (const id of ['identity', 'state', 'pins', 'execution', 'status', 'prompt', 'timeline'])
      await expect(page.getByTestId(`task-core-${id}`)).toBeVisible();

    await setTheme(page, 'light');
    await shot('task-core-local-core-light.png');
    await setTheme(page, 'dark');
    await shot('task-core-local-core-dark.png');
    releaseDocuments!();
    if (process.env.PERF_WORKSTATION_GATE === '1')
      expect(cachedSwitch.p95Ms, 'workstation switch to core-ready p95').toBeLessThanOrEqual(100);
  } finally {
    releaseDocuments?.();
    for (const id of ids) {
      await api(`/api/tasks/${encodeURIComponent(id)}?watchPath=${encodeURIComponent(watchPath)}`,
        { method: 'DELETE', signal: AbortSignal.timeout(10_000) })
        .catch(() => undefined);
    }
  }
});
