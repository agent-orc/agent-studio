import { test, expect, type Page, type Response } from '@playwright/test';
import { mkdir, writeFile } from 'node:fs/promises';
import { spawnSync } from 'node:child_process';
import { resolve } from 'node:path';

/** Machine-bound acceptance capture. Stable is read-only; faults need isolated fixtures. */
const enabled = process.env.TASK_SWITCH_BUDGET === '1';
test.use({ trace: 'off', video: 'off' });
const diagnostic = process.env.TASK_SWITCH_DIAGNOSTIC === '1';
const count = Number(process.env.TASK_SWITCH_COUNT ?? 100);
const warmups = Number(process.env.TASK_SWITCH_WARMUPS ?? 5);
const output = resolve(process.env.TASK_SWITCH_OUTPUT ?? process.env.JOB_RESULTS_DIR ?? 'test-results',
  process.env.TASK_SWITCH_OUTPUT ? '' : 'task-switch-budget.json');
const profile = process.env.TASK_SWITCH_PROFILE ?? '';
const keys = {
  active: process.env.TASK_SWITCH_ACTIVE_KEY ?? '',
  review: process.env.TASK_SWITCH_REVIEW_KEY ?? '',
  archived: process.env.TASK_SWITCH_ARCHIVED_KEY ?? '',
  long: process.env.TASK_SWITCH_LONG_KEY ?? '',
};
type CoreRequest = { status: number; bytes: number | null; gitSpawns: number | null;
  workspaceScans: number | null; coreState: string | null;
  coreVersion: string | null;
  serverTiming: Record<string, number>; requestId: string | null; responseSwitchId: string | null;
  waterfall: { startMs: number; durationMs: number; transferBytes: number; decodedBytes: number } | null };
type Sample = { outcome: string; paintMs?: number; coreFacts?: boolean; groupedReads: number;
  coreRequests: CoreRequest[]; resources?: unknown[]; error?: string; switchId: string;
  target: string; projectId: string; input: string; sampleClass: string;
  startedAtUtc: string; finishedAtUtc?: string; inputAtEpochMs?: number | null;
  resourceGenerations: Record<string, string> };

async function arm(page: Page, target: string, input: 'click' | 'keydown' | 'popstate' | 'navigation') {
  await page.evaluate(({ target, input }) => {
    const win = window as typeof window & { __budget?: { started: number | null; startedEpoch: number | null;
      painted: number | null; facts: boolean; id: string | null } };
    const state = { started: input === 'navigation' ? 0 : null, startedEpoch: null as number | null,
      painted: null, facts: false, id: null as string | null };
    win.__budget = state;
    const onInput = (event: Event) => {
      if (event.type === input && state.started === null) {
        state.started = event.timeStamp;
        state.startedEpoch = performance.timeOrigin + event.timeStamp;
      }
    };
    if (input !== 'navigation') window.addEventListener(input, onInput, { once: true, capture: true });
    const observe = () => {
      if (state.started === null || state.painted !== null) return;
      const root = document.querySelector('[data-testid="task-core"]');
      if (!root) return;
      const renderedId = root.getAttribute('data-core-id')?.toLowerCase();
      if (!renderedId || (target.startsWith('!:')
        ? renderedId === target.slice(2).toLowerCase()
        : renderedId !== target.toLowerCase())) return;
      if (!root.getAttribute('data-core-version')) return;
      for (const name of ['state', 'pins', 'execution', 'status', 'prompt', 'timeline']) {
        const section = root.querySelector(`[data-testid="task-core-${name}"]`);
        const text = (section?.textContent ?? '').replace(section?.querySelector('h2')?.textContent ?? '', '').trim();
        if (!text || /^(loading|warming)(\s|$)/i.test(text)) return;
      }
      if (!document.querySelector('[data-testid="task-core-identity"]')?.textContent?.trim()) return;
      state.id = renderedId;
      state.facts = true;
      requestAnimationFrame(() => requestAnimationFrame(() => {
        if (state.painted === null) state.painted = performance.now() - state.started!;
      }));
    };
    new MutationObserver(observe).observe(document.documentElement, { childList: true, subtree: true, characterData: true });
    observe();
  }, { target, input });
}

async function painted(page: Page, navigation = false, target = ''): Promise<{ ms: number; facts: boolean }> {
  await expect.poll(() => page.evaluate(({ navigation, target }) => {
    const state = navigation ? (window as any).__budgetNavigation : (window as any).__budget;
    if (navigation && state?.id?.toLowerCase() !== target.toLowerCase()) return null;
    return state?.painted ?? null;
  }, { navigation, target }),
    { timeout: 10_000, intervals: [10, 20, 50] }).not.toBeNull();
  return page.evaluate(navigation => {
    const state = navigation ? (window as any).__budgetNavigation : (window as any).__budget;
    return { ms: state.painted, facts: state.facts };
  }, navigation);
}

async function clickDeepLink(page: Page, target: string, onArmed: () => void): Promise<void> {
  await page.goto('/');
  await page.evaluate(key => {
    const link = document.createElement('a');
    link.id = 'task-switch-measurement-link';
    link.href = `/?job=${encodeURIComponent(key)}`;
    link.textContent = 'Open measured task';
    link.style.cssText = 'position:fixed;top:4px;left:4px;z-index:99999';
    link.addEventListener('click', event => {
      sessionStorage.setItem('__taskSwitchInputEpoch', String(performance.timeOrigin + event.timeStamp));
    }, { capture: true, once: true });
    document.body.append(link);
  }, target);
  onArmed();
  await page.locator('#task-switch-measurement-link').click();
}

function parseCounter(value: string | undefined): number | null {
  return value !== undefined && /^\d+$/.test(value) ? Number(value) : null;
}

test('task switch budget on the designated workstation', async ({ page, baseURL }) => {
  test.skip(!enabled, 'Machine-bound performance suite runs with TASK_SWITCH_BUDGET=1');
  test.setTimeout(90 * 60_000);
  if (!profile || Object.values(keys).some(key => !key))
    throw Error('TASK_SWITCH_PROFILE and active, review, archived, long task keys are required');
  if (!diagnostic && (count < 100 || warmups < 5))
    throw Error('At least 100 samples and five warmups are required');
  const remote = process.env.TASK_SWITCH_PROFILE_KIND !== 'designated-workstation';
  const capture: any = { profile: { name: profile, kind: remote ? 'remote-browser' : 'designated-workstation',
    backendReadiness: 'unverified', origin: baseURL, source: process.env.TASK_SWITCH_SOURCE ?? 'unknown',
    startedAt: new Date().toISOString(), coldProcessStartupMs: null },
    pageErrors: [] as string[], requestErrors: [] as unknown[], apiErrors: [] as unknown[],
    cohorts: {}, screenshots: [] as string[], maxConcurrentCoreRequests: 0 };
  const responsePromises: Promise<void>[] = [];
  let current: Sample | null = null;
  let concurrentCore = 0;
  const requestSwitch = new WeakMap<object, string>();
  const sampleBySwitch = new Map<string, Sample>();
  page.on('pageerror', error => capture.pageErrors.push(error.message));
  page.on('request', request => {
    if (/\/api\/tasks\/[^/]+\/core(?:\?|$)/.test(request.url())) {
      concurrentCore++;
      capture.maxConcurrentCoreRequests = Math.max(capture.maxConcurrentCoreRequests, concurrentCore);
    }
    if (!current) return;
    requestSwitch.set(request, current.switchId);
    if (request.url().includes('/api/v1/studio/board')) current.groupedReads++;
  });
  page.on('requestfinished', request => {
    if (/\/api\/tasks\/[^/]+\/core(?:\?|$)/.test(request.url())) concurrentCore--;
  });
  page.on('requestfailed', request => {
    if (/\/api\/tasks\/[^/]+\/core(?:\?|$)/.test(request.url())) concurrentCore--;
    if (request.url().includes('/api/')) capture.requestErrors.push({
      path: new URL(request.url()).pathname, failure: request.failure()?.errorText ?? 'unknown',
      switchId: requestSwitch.get(request) ?? null,
    });
  });
  await page.route(/\/api\/tasks\/[^/]+\/core(?:\?|$)/, route => route.continue({ headers: {
    ...route.request().headers(), 'x-task-switch-trace': '1',
    'x-task-switch-id': current?.switchId ?? crypto.randomUUID(),
    'x-task-request-id': crypto.randomUUID(),
  } }));
  page.on('response', (response: Response) => {
    if (response.url().includes('/api/') && response.status() >= 400)
      capture.apiErrors.push({ path: new URL(response.url()).pathname, status: response.status(),
        switchId: requestSwitch.get(response.request()) ?? null });
    if (/\/api\/tasks\/[^/]+\/details\//.test(response.url())) {
      const id = requestSwitch.get(response.request());
      const sample = id && sampleBySwitch.get(id);
      const etag = response.headers()['etag'];
      if (sample && etag) sample.resourceGenerations[new URL(response.url()).pathname] = etag;
    }
    if (!/\/api\/tasks\/[^/]+\/core(?:\?|$)/.test(response.url())) return;
    const id = requestSwitch.get(response.request());
    const sample = id && sampleBySwitch.get(id);
    if (!sample) return;
    responsePromises.push((async () => {
      const headers = await response.allHeaders();
      let bytes: number | null = response.status() === 304 ? 0 : null;
      let coreState: string | null = null;
      let coreVersion: string | null = null;
      try {
        const body = response.status() === 304 ? Buffer.alloc(0) : await response.body();
        bytes = body.byteLength;
        if (response.status() === 200) {
          const parsed = JSON.parse(body.toString('utf8'));
          coreState = parsed.state ?? null;
          coreVersion = parsed.coreVersion ?? null;
        }
      } catch { /* Missing bytes or state fail the gate. */ }
      const serverTiming: Record<string, number> = {};
      for (const entry of (headers['server-timing'] ?? '').split(',')) {
        const match = entry.trim().match(/^([\w-]+);dur=([\d.]+)/);
        if (match) serverTiming[match[1]] = Number(match[2]);
      }
      sample.coreRequests.push({ status: response.status(), bytes, coreState, coreVersion,
        gitSpawns: parseCounter(headers['x-task-core-git-spawns']),
        workspaceScans: parseCounter(headers['x-task-core-workspace-scans']),
        serverTiming, requestId: headers['x-task-request-id'] ?? null,
        responseSwitchId: headers['x-task-switch-id'] ?? null,
        waterfall: null });
    })());
  });
  await page.addInitScript(() => {
    performance.setResourceTimingBufferSize(4096);
    let inputEpoch: number | null = null;
    try {
      const stored = sessionStorage.getItem('__taskSwitchInputEpoch');
      if (stored !== null && Number.isFinite(Number(stored))) inputEpoch = Number(stored);
      sessionStorage.removeItem('__taskSwitchInputEpoch');
    } catch { /* A missing navigation mark fails the sample. */ }
    const state = { painted: null as number | null, facts: false, id: null as string | null,
      inputEpoch };
    (window as any).__budgetNavigation = state;
    const check = () => {
      if (state.painted !== null) return;
      const root = document.querySelector('[data-testid="task-core"]');
      const id = root?.getAttribute('data-core-id');
      if (!id || !root?.getAttribute('data-core-version')) return;
      for (const name of ['state', 'pins', 'execution', 'status', 'prompt', 'timeline']) {
        const section = root.querySelector(`[data-testid="task-core-${name}"]`);
        const text = (section?.textContent ?? '').replace(section?.querySelector('h2')?.textContent ?? '', '').trim();
        if (!text || /^(loading|warming)(\s|$)/i.test(text)) return;
      }
      if (!document.querySelector('[data-testid="task-core-identity"]')?.textContent?.trim()) return;
      state.id = id;
      state.facts = true;
      requestAnimationFrame(() => requestAnimationFrame(() => {
        if (state.inputEpoch !== null)
          state.painted = performance.timeOrigin + performance.now() - state.inputEpoch;
      }));
    };
    new MutationObserver(check).observe(document, { childList: true, subtree: true, characterData: true });
  });
  try {
    const apiUrl = process.env.TASK_SWITCH_API_URL
      ?? (process.env.PW_TARGET === 'stable' ? 'http://127.0.0.1:5031' : 'http://127.0.0.1:5030');
    const ready = await page.request.get(`${apiUrl}/healthz`, { timeout: 5000 });
    if (!ready.ok()) throw Error('Backend was not ready before capture');
    capture.profile.backendReadiness = 'ready-before-capture';
    const version = await page.request.get('/api/system/version', { timeout: 5000 });
    if (version.ok()) capture.profile.backendVersion = await version.json();
    const boot = Date.now();
    await page.goto('/', { waitUntil: 'domcontentloaded' });
    await page.getByTestId('task-card').first().waitFor();
    capture.profile.coldClientReadyMs = Date.now() - boot;
    if (await page.getByTestId('crash-recovery-prompt-overlay').isVisible())
      throw Error('Shared crash recovery overlay obstructs task navigation; no recovery action taken');
    const plans = [
      { name: 'board-click', key: keys.active, input: 'click', cached: false, taskClass: 'active' },
      { name: 'pager-keyboard', key: keys.active, input: 'keydown', cached: false, taskClass: 'active' },
      { name: 'back-forward', key: keys.review, input: 'popstate', cached: false, taskClass: 'review' },
      { name: 'deep-link', key: keys.archived, input: 'navigation', cached: false, taskClass: 'archived' },
      { name: 'long-document', key: keys.long, input: 'navigation', cached: false, taskClass: 'long-document' },
      { name: 'cold-client', key: keys.active, input: 'navigation', cached: false, taskClass: 'active' },
      { name: 'cached-core', key: keys.active, input: 'click', cached: true, taskClass: 'active' },
    ] as const;
    const selectedPlans = process.env.TASK_SWITCH_ONLY
      ? plans.filter(plan => plan.name === process.env.TASK_SWITCH_ONLY) : plans;
    for (const theme of ['light', 'dark']) for (const plan of selectedPlans) {
      const name = `${theme}/${plan.name}`;
      const population = { warmups, cached: plan.cached,
        warmupSamples: [] as Sample[], samples: [] as Sample[] };
      capture.cohorts[name] = population;
      if (plan.cached) await page.goto('/');
      await page.evaluate(value => {
        document.documentElement.dataset['studioTheme'] = value;
        localStorage.setItem('atp.studio.theme', value);
      }, theme);
      for (let i = -warmups; i < count; i++) {
        const sample: Sample = { outcome: 'error', groupedReads: 0, coreRequests: [],
          switchId: crypto.randomUUID().replaceAll('-', ''), target: plan.key,
          projectId: process.env.TASK_SWITCH_PROJECT ?? 'PROJ-002', input: plan.input,
          sampleClass: plan.taskClass, startedAtUtc: new Date().toISOString(), resourceGenerations: {} };
        sampleBySwitch.set(sample.switchId, sample);
        try {
          if (plan.input === 'click') {
            if (!plan.cached) await page.goto('/');
            else await page.getByTestId('studio-board').waitFor();
            const card = page.getByTestId('task-card').filter({ hasText: plan.key }).first();
            await card.waitFor();
            await arm(page, plan.key, 'click');
            current = sample;
            await card.click();
          } else if (plan.input === 'keydown') {
            await page.goto(`/?job=${encodeURIComponent(plan.key)}`);
            const position = await page.getByTestId('studio-task-pager-position').innerText();
            const key = position.trim().startsWith('1 ') ? 'j' : 'k';
            await arm(page, `!:${plan.key}`, 'keydown');
            current = sample;
            await page.keyboard.press(key);
          } else if (plan.input === 'popstate') {
            await page.goto(`/?job=${encodeURIComponent(plan.key)}`);
            const position = await page.getByTestId('studio-task-pager-position').innerText();
            await page.getByTestId(position.trim().startsWith('1 ') ? 'studio-task-next' : 'studio-task-prev').click();
            await arm(page, plan.key, 'popstate');
            current = sample;
            await page.goBack();
          } else {
            await clickDeepLink(page, plan.key, () => { current = sample; });
          }
          const result = await painted(page, plan.input === 'navigation', plan.key);
          if (plan.input === 'keydown')
            sample.target = await page.evaluate(() => (window as any).__budget?.id ?? 'unknown');
          sample.outcome = 'ok'; sample.paintMs = result.ms; sample.coreFacts = result.facts;
          sample.inputAtEpochMs = await page.evaluate(navigation => navigation
            ? (window as any).__budgetNavigation?.inputEpoch ?? null
            : (window as any).__budget?.startedEpoch ?? null,
            plan.input === 'navigation');
          if (await page.evaluate(() => document.documentElement.dataset['studioTheme']) !== theme)
            throw Error(`Expected ${theme} theme at core paint`);
          sample.resources = await page.evaluate(navigation => {
            const since = navigation ? 0 : (window as any).__budget?.started ?? 0;
            return (performance.getEntriesByType('resource') as PerformanceResourceTiming[])
            .filter(entry => entry.name.includes('/api/') && entry.startTime >= since).slice(-128).map(entry => ({
              path: new URL(entry.name).pathname, startMs: entry.startTime, durationMs: entry.duration,
              ttfbMs: entry.responseStart - entry.requestStart, transferBytes: entry.transferSize,
              decodedBytes: entry.decodedBodySize,
            }));
          }, plan.input === 'navigation');
        } catch (error) {
          sample.outcome = 'error';
          sample.error = String(error).slice(0, 300);
        }
        await Promise.allSettled(responsePromises.splice(0));
        sample.finishedAtUtc = new Date().toISOString();
        const coreResources = (sample.resources ?? []).filter((entry: any) => /\/api\/tasks\/[^/]+\/core$/.test(entry.path));
        sample.coreRequests.forEach((request, index) => { request.waterfall = coreResources[index] as CoreRequest['waterfall'] ?? null; });
        if (i >= 0) population.samples.push(sample);
        else population.warmupSamples.push(sample);
        if (i === 0) {
          const screenshot = `task-switch-${theme}-${plan.name}.png`;
          await page.screenshot({ path: resolve(output, '..', screenshot), timeout: 10_000,
            animations: 'disabled' });
          capture.screenshots.push(screenshot);
        }
        current = null;
        if (plan.cached && sample.outcome === 'ok')
          await page.getByTestId('studio-tab-board:__all__').click();
      }
    }
  } catch (error) {
    capture.failure = String(error).slice(0, 400);
    throw error;
  } finally {
    capture.profile.finishedAt = new Date().toISOString();
    await mkdir(resolve(output, '..'), { recursive: true });
    await writeFile(output, JSON.stringify(capture, null, 2) + '\n');
  }
  const summary = resolve(output, '..', 'task-switch-budget-summary.json');
  if (diagnostic || process.env.TASK_SWITCH_DEFER_EVAL === '1') return;
  const evaluator = resolve(__dirname, '../../../docs/task-switch-performance/evaluate-budget.mjs');
  const verdict = spawnSync(process.execPath, [evaluator, output, summary], { encoding: 'utf8' });
  expect(verdict.status, verdict.stderr || `Budget failed; see ${summary}`).toBe(0);
});
