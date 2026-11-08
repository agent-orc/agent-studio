import { expect, test } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';
import { hostname } from 'node:os';
import { join } from 'node:path';
import { installSyntheticBoard } from '../board/large-board.fixture';

const results = process.env.JOB_RESULTS_DIR ?? 'test-results';
const median = (values: number[]) => [...values].sort((a, b) => a - b)[1];

test.describe.configure({ mode: 'parallel' });

for (const size of [20, 830, 3000]) {
  test(`generated board window benchmark at ${size} cards`, async ({ browser }) => {
    test.skip(process.env.RUN_BOARD_WINDOW_BENCH !== '1', 'Set RUN_BOARD_WINDOW_BENCH=1 for the three-run benchmark.');
    test.setTimeout(300_000);
    const samples: Record<'cold' | 'warm', {
      firstBoardPaintMs: number; navigationWaitMs: number; longestTaskMs: number; domNodes: number; heapMiB: number;
    }[]> = { cold: [], warm: [] };

    for (let run = 0; run < 3; run++) {
      const context = await browser.newContext({ viewport: { width: 1280, height: 800 } });
      const page = await context.newPage();
      await page.addInitScript(expectedCount => {
        const probe = window as Window & { boardLongTasks?: number[]; boardPaintMs?: number };
        probe.boardLongTasks = [];
        probe.boardPaintMs = 0;
        new PerformanceObserver(list => {
          const tasks = probe.boardLongTasks!;
          for (const entry of list.getEntries()) tasks.push(entry.duration);
        }).observe({ type: 'longtask', buffered: true });
        const mutations = new MutationObserver(() => {
          const count = document.querySelector('[data-testid="lane-count-2-ready"]');
          const card = document.querySelector('[data-testid="lane-scroll-2-ready"] app-job-card');
          if (!count?.textContent?.includes(String(expectedCount)) || !card) return;
          mutations.disconnect();
          requestAnimationFrame(() => { probe.boardPaintMs = performance.now(); });
        });
        mutations.observe(document, { childList: true, characterData: true, subtree: true });
      }, size);
      await installSyntheticBoard(page, size);
      const cdp = await context.newCDPSession(page);
      await cdp.send('Performance.enable');

      for (const temperature of ['cold', 'warm'] as const) {
        const start = performance.now();
        await page.goto('/', { waitUntil: 'domcontentloaded' });
        await expect(page.getByTestId('lane-count-2-ready')).toContainText(String(size), { timeout: 30_000 });
        await expect(page.getByTestId('lane-scroll-2-ready').locator('app-job-card').first()).toBeVisible();
        await expect.poll(() => page.evaluate(() =>
          (window as Window & { boardPaintMs?: number }).boardPaintMs ?? 0)).toBeGreaterThan(0);
        const firstBoardPaintMs = await page.evaluate(() =>
          (window as Window & { boardPaintMs?: number }).boardPaintMs ?? 0);
        const navigationWaitMs = performance.now() - start;
        const longestTaskMs = await page.evaluate(() => Math.max(0, ...((window as Window & { boardLongTasks?: number[] }).boardLongTasks ?? [])));
        await cdp.send('HeapProfiler.collectGarbage');
        const metrics = await cdp.send('Performance.getMetrics');
        const heapBytes = metrics.metrics.find(metric => metric.name === 'JSHeapUsedSize')?.value ?? 0;
        const domNodes = await page.evaluate(() => document.getElementsByTagName('*').length);
        samples[temperature].push({ firstBoardPaintMs, navigationWaitMs, longestTaskMs, domNodes, heapMiB: heapBytes / 1024 / 1024 });
      }
      await context.close();
    }

    const summary = Object.fromEntries((['cold', 'warm'] as const).map(temperature => [temperature, {
      firstBoardPaintMs: median(samples[temperature].map(sample => sample.firstBoardPaintMs)),
      longestTaskMs: median(samples[temperature].map(sample => sample.longestTaskMs)),
      domNodes: median(samples[temperature].map(sample => sample.domNodes)),
      heapMiB: median(samples[temperature].map(sample => sample.heapMiB)),
    }]));
    mkdirSync(results, { recursive: true });
    writeFileSync(join(results, `board-window-${size}--generated.json`), JSON.stringify({
      size, host: hostname(), browser: browser.version(), samples, summary,
    }, null, 2));
    console.log(`BOARD_WINDOW ${size}: ${JSON.stringify(summary)}`);
  });
}

test('generated 830-card menu moves paint within the lane budget', async ({ browser }) => {
  test.skip(process.env.RUN_BOARD_WINDOW_BENCH !== '1', 'Set RUN_BOARD_WINDOW_BENCH=1 for the three-run benchmark.');
  test.setTimeout(120_000);
  const samples: number[] = [];
  for (let run = 0; run < 3; run++) {
    const context = await browser.newContext({ viewport: { width: 1280, height: 800 } });
    const page = await context.newPage();
    await installSyntheticBoard(page, 830);
    await page.route('**/api/v1/projects/*/tasks/*/move*', async route => {
      await new Promise(resolve => setTimeout(resolve, 1000));
      await route.fulfill({ status: 200, json: {} });
    });
    await page.goto('/');
    await expect(page.getByTestId('lane-count-2-ready')).toContainText('830');
    await page.getByTestId('lane-scroll-2-ready').locator('app-job-card').first().click({ button: 'right' });
    await page.getByRole('menuitem', { name: 'Move to Accepted' }).waitFor();
    const elapsed = await page.evaluate(() => new Promise<number>((resolve, reject) => {
      const counter = document.querySelector<HTMLElement>('[data-testid="lane-count-2-ready"]');
      const item = Array.from(document.querySelectorAll<HTMLElement>('[role="menuitem"]'))
        .find(element => element.textContent?.trim() === 'Move to Accepted');
      if (!counter || !item) { reject(new Error('Move control or lane count missing')); return; }
      const start = performance.now();
      const observer = new MutationObserver(() => {
        if (!counter.textContent?.includes('829')) return;
        observer.disconnect();
        requestAnimationFrame(() => resolve(performance.now() - start));
      });
      observer.observe(counter, { childList: true, subtree: true, characterData: true });
      item.click();
      setTimeout(() => { observer.disconnect(); reject(new Error('Optimistic lane move did not paint')); }, 5000);
    }));
    samples.push(elapsed);
    await context.close();
  }
  mkdirSync(results, { recursive: true });
  writeFileSync(join(results, 'board-window-lane-move--generated.json'), JSON.stringify({
    host: hostname(), browser: browser.version(), samples, medianMs: median(samples),
  }, null, 2));
  console.log(`BOARD_WINDOW lane move: median ${median(samples).toFixed(1)}ms`);
});
