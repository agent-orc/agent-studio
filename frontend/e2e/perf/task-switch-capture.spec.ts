import { test, type Page } from '@playwright/test';
import { mkdir, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';

// Read-only workstation capture. Opt in explicitly; no fixtures or API writes.
const enabled = process.env.TASK_SWITCH_CAPTURE === '1';
const count = Number(process.env.TASK_SWITCH_COUNT ?? 30);
const keys = (process.env.TASK_SWITCH_KEYS ?? 'AGT-2797,AGT-2910,AGT-2736,AGT-2860')
  .split(',').map(value => value.trim()).filter(Boolean);
const classes = (process.env.TASK_SWITCH_CLASSES ?? 'small,long-history,active,archived')
  .split(',').map(value => value.trim());
if (classes.length !== keys.length) throw Error('TASK_SWITCH_CLASSES must align with TASK_SWITCH_KEYS');
const taskClass = new Map(keys.map((key, index) => [key.toUpperCase(), classes[index]]));
const boardKeys = keys.filter(key => taskClass.get(key.toUpperCase()) !== 'archived');
if (!boardKeys.length) throw Error('At least one non-archived board key is required');
const output = resolve(process.env.TASK_SWITCH_OUTPUT ?? 'docs/task-switch-performance/evidence/workstation-capture.json');
const trace = process.env.TASK_SWITCH_TRACE === '1';

test('capture real task switches', async ({ page, baseURL }) => {
  test.skip(!enabled, 'Read-only capture is opt-in');
  test.setTimeout(30 * 60_000);
  const report: Record<string, unknown> = {
    sourceRevision: 'unknown',
    runEnvironment: process.env.TASK_SWITCH_ENVIRONMENT ?? 'unspecified workstation',
    origin: baseURL, startedAt: new Date().toISOString(),
    coldClient: true, backendReadiness: 'already running',
    countPerCohort: count, keys, classes, samples: [] as unknown[], errors: [] as string[],
    requestEvents: [] as unknown[],
  };
  const samples = report.samples as Record<string, unknown>[];
  const errors = report.errors as string[];
  page.on('pageerror', error => errors.push(error.message));
  let switchId = crypto.randomUUID();
  const requestEvents = report.requestEvents as Record<string, unknown>[];
  const requestSwitches = new WeakMap<object, string>();
  page.on('request', request => requestSwitches.set(request, switchId));
  if (trace) {
    await page.route('**/api/**', async route => {
      const requestId = crypto.randomUUID();
      const entry: Record<string, unknown> = {
        switchId, requestId, path: new URL(route.request().url()).pathname,
        method: route.request().method(), startedAt: new Date().toISOString(),
      };
      if (requestEvents.length < 10000) requestEvents.push(entry);
      await route.continue({ headers: {
        ...route.request().headers(), 'x-task-switch-trace': '1',
        'x-task-switch-id': switchId, 'x-task-request-id': requestId,
      } });
    });
  }
  page.on('response', response => {
    if (!response.url().includes('/api/')) return;
    const entry = { switchId: requestSwitches.get(response.request()) ?? null,
      path: new URL(response.url()).pathname,
      status: response.status(), at: new Date().toISOString(),
      requestId: response.headers()['x-task-request-id'] ?? null };
    if (requestEvents.length < 10000) requestEvents.push(entry);
  });
  await page.addInitScript(() => {
    performance.setResourceTimingBufferSize(10000);
    localStorage.setItem('perf', '1');
  });
  page.setDefaultNavigationTimeout(120_000);
  const coldStarted = Date.now();
  try {
    const version = await page.request.get('/api/system/version');
    if (version.ok()) {
      const body = await version.json();
      report.sourceRevision = body.commit ?? 'unknown';
      report.sourceVersion = body.version ?? 'unknown';
    }
    await page.goto('/', { waitUntil: 'commit' });
    await page.getByTestId('task-card').first().waitFor({ timeout: 120_000 });
    report.coldClientReadyMs = Date.now() - coldStarted;
    report.backendReadiness = 'ready before capture; process startup excluded';
    if (await page.getByTestId('crash-recovery-prompt-overlay').isVisible())
      throw Error('Shared crash recovery overlay obstructs navigation; no recovery action taken');
    for (const cohort of ['board-click', 'pager', 'back-forward', 'deep-link']) {
      for (let i = 0; i < count; i++) {
        switchId = crypto.randomUUID();
        const target = cohort === 'board-click'
          ? boardKeys[i % boardKeys.length] : keys[i % keys.length];
        const sample: Record<string, unknown> = {
          cohort, ordinal: i + 1, target, switchId,
          sampleClass: taskClass.get(target.toUpperCase()) ?? 'unclassified',
        };
        try {
          let previousKey: string | null = null;
          if (cohort === 'board-click') {
            if (i > 0) await page.goBack({ waitUntil: 'commit' });
            const card = page.getByTestId('task-card').filter({ hasText: target }).first();
            await card.waitFor({ timeout: 15000 });
            await arm(page, 'click');
            await card.click();
          } else if (cohort === 'pager') {
            await page.goto(`/?job=${encodeURIComponent(target)}`, { waitUntil: 'commit' });
            previousKey = await page.getByTestId('overview-title-key').innerText();
            await arm(page, 'click');
            await page.getByTestId(i % 2 ? 'studio-task-prev' : 'studio-task-next').click();
          } else if (cohort === 'back-forward') {
            await page.goto(`/?job=${encodeURIComponent(target)}`, { waitUntil: 'commit' });
            await page.goto(`/?job=${encodeURIComponent(keys[(i + 1) % keys.length])}`, { waitUntil: 'commit' });
            if (i % 2) await page.goBack();
            previousKey = await page.getByTestId('overview-title-key').innerText();
            await arm(page, 'popstate');
            if (i % 2) await page.goForward(); else await page.goBack();
          } else {
            await page.goto(`/?job=${encodeURIComponent(target)}`, { waitUntil: 'commit' });
          }
          sample.outcome = 'ok';
          Object.assign(sample, await painted(page, cohort === 'deep-link', previousKey));
          const observed = String(sample.key ?? '').toUpperCase();
          sample.sampleClass = [...taskClass].find(([key]) => observed.includes(key))?.[1] ?? 'unclassified';
        } catch (error) {
          sample.outcome = 'error';
          sample.error = String(error).slice(0, 300);
        }
        samples.push(sample);
        if (sample.outcome === 'error') throw Error(`Capture stopped after ${cohort} switch ${i + 1}: ${sample.error}`);
      }
    }
  } catch (error) {
    report.failure = String(error).slice(0, 300);
    throw error;
  } finally {
    report.finishedAt = new Date().toISOString();
    await mkdir(resolve(output, '..'), { recursive: true });
    await writeFile(output, JSON.stringify(report, null, 2) + '\n');
  }
});

async function arm(page: Page, event: 'click' | 'popstate') {
  await page.evaluate(kind => {
    performance.clearMarks(); performance.clearMeasures(); performance.clearResourceTimings();
    (window as any).__taskSwitchStart = undefined;
    window.addEventListener(kind, () => { (window as any).__taskSwitchStart = performance.now(); },
      { once: true, capture: true });
  }, event);
}

async function painted(page: Page, navigation: boolean, previousKey: string | null) {
  return page.evaluate(async ({ isNavigation, previous }) => {
    const began = isNavigation ? 0 : (window as any).__taskSwitchStart;
    if (typeof began !== 'number') throw Error('Input event was not observed');
    const deadline = performance.now() + 20000;
    while (performance.now() < deadline) {
      const key = document.querySelector('[data-testid="overview-title-key"]');
      const prompt = document.querySelector('[data-testid="pane-prompt-body"]');
      if (key?.textContent?.trim() && key.textContent.trim() !== previous?.trim()
          && prompt && prompt.getBoundingClientRect().height > 0) break;
      await new Promise<void>(resolve => requestAnimationFrame(() => resolve()));
    }
    const key = document.querySelector('[data-testid="overview-title-key"]');
    const prompt = document.querySelector('[data-testid="pane-prompt-body"]');
    if (!key?.textContent?.trim() || key.textContent.trim() === previous?.trim()
        || !prompt || prompt.getBoundingClientRect().height <= 0)
      throw Error('Detail DOM did not become ready');
    performance.mark('task-switch-dom-ready');
    const domReadyMs = performance.now() - began;
    const signal = performance.getEntriesByType('measure')
      .filter(e => e.name === 'job-select-to-rendered').at(-1);
    const domWorkMs = signal ? Math.max(0,
      performance.getEntriesByName('task-switch-dom-ready', 'mark')[0].startTime
        - (signal.startTime + signal.duration)) : null;
    await new Promise<void>(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve())));
    const paintOpportunityMs = performance.now() - began;
    return {
      key: key.textContent.trim(),
      domReadyMs, paintOpportunityMs,
      resources: performance.getEntriesByType('resource').filter(e => e.name.includes('/api/')).slice(-256)
        .map(entry => {
          const r = entry as PerformanceResourceTiming;
          return { path: new URL(r.name).pathname, startMs: r.startTime - began,
            durationMs: r.duration, ttfbMs: r.responseStart - r.requestStart,
            decodedBytes: r.decodedBodySize, transferBytes: r.transferSize,
            serverTiming: r.serverTiming.map(t => ({ name: t.name, durationMs: t.duration })) };
        }),
      markdownConversion: performance.getEntriesByType('measure')
        .filter(e => /markdown|beautiful-results-render/.test(e.name)).slice(-64)
        .map(e => ({ name: e.name, durationMs: e.duration })),
      domWorkMs, paintWaitMs: paintOpportunityMs - domReadyMs,
    };
  }, { isNavigation: navigation, previous: previousKey });
}
