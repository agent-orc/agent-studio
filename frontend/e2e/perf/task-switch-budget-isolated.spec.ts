import { test, expect } from '../fixtures/dev-backend';
import type { Page } from '@playwright/test';
import { mkdir, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';

const enabled = process.env.TASK_SWITCH_BUDGET === '1';
const diagnostic = process.env.TASK_SWITCH_DIAGNOSTIC === '1';
const count = Number(process.env.TASK_SWITCH_COUNT ?? 100);
const warmups = Number(process.env.TASK_SWITCH_WARMUPS ?? 5);
const output = resolve(process.env.JOB_RESULTS_DIR ?? 'test-results', diagnostic
  ? 'task-switch-isolated-diagnostic.json' : 'task-switch-isolated.json');
test.use({ trace: 'off', video: 'off' });

async function showBoard(page: Page): Promise<void> {
  await page.getByTestId('studio-tab-board:__all__').dispatchEvent('click', {}, { timeout: 10_000 });
  await expect(page.getByTestId('studio-board')).toBeVisible({ timeout: 30_000 });
}

test.describe('task switch isolated budget cohorts', () => {
  test.describe.configure({ timeout: 90 * 60_000 });
  test.skip(!enabled, 'The machine-bound budget suite is opt-in');

  test('dirty index, own mutation and Git resource faults keep core responsive', async ({ page, devBackend }) => {
    test.setTimeout(90 * 60_000);
    if (!diagnostic && (count < 100 || warmups < 5))
      throw Error('Isolated cohorts require 100 samples after five warmups');
    const startedAt = new Date().toISOString();
    const watchResponse = await fetch(`${devBackend.baseUrl}/api/watch-paths`);
    if (!watchResponse.ok) throw Error(`Watch path lookup failed: ${watchResponse.status}`);
    const watchPath = ((await watchResponse.json()) as { path: string }[])[0]?.path;
    if (!watchPath) throw Error('The isolated backend has no watch path');
    const workspaceResponse = await fetch(`${devBackend.baseUrl}/api/v1/workspaces`);
    if (!workspaceResponse.ok) throw Error(`Project lookup failed: ${workspaceResponse.status}`);
    const workspaces = await workspaceResponse.json() as { projects?: { id: string; storageLocation: string }[] }[];
    const samePath = (left: string, right: string) =>
      left.replaceAll('\\', '/').replace(/\/$/, '').toLowerCase()
      === right.replaceAll('\\', '/').replace(/\/$/, '').toLowerCase();
    const projectId = workspaces.flatMap(workspace => workspace.projects ?? [])
      .find(project => samePath(project.storageLocation, watchPath))?.id;
    if (!projectId) throw Error('No isolated project matches the backend watch path');
    const prefix = `e2e-switch-budget-${Date.now()}-${Math.floor(Math.random() * 10000)}`;
    const taskClasses = [
      { name: 'active', state: '2-ready', ids: [`${prefix}-active-a`, `${prefix}-active-b`] },
      { name: 'review', state: '5-human-review', ids: [`${prefix}-review-a`, `${prefix}-review-b`] },
      { name: 'archived', state: '7-archive', ids: [`${prefix}-archived-a`, `${prefix}-archived-b`] },
    ] as const;
    const unrelatedId = `${prefix}-unrelated`;
    const report: any = {
      profile: { name: process.env.TASK_SWITCH_PROFILE ?? 'unspecified',
        kind: process.env.TASK_SWITCH_PROFILE_KIND === 'designated-workstation'
          ? 'designated-workstation' : 'remote-browser',
        backendReadiness: 'ready-before-capture',
        backend: devBackend.baseUrl, source: process.env.TASK_SWITCH_SOURCE ?? 'worktree',
        fixture: 'isolated dev-backend task repository', startedAt,
        coldProcessStartupMs: null },
      cohorts: {}, pageErrors: [] as string[], requestErrors: [] as unknown[], apiErrors: [] as unknown[],
      screenshots: [] as string[], maxConcurrentCoreRequests: 0,
    };
    const versionResponse = await fetch(`${devBackend.baseUrl}/api/system/version`);
    if (versionResponse.ok) report.profile.backendVersion = await versionResponse.json();
    const api = async (path: string, init?: RequestInit) => {
      const response = await fetch(`${devBackend.baseUrl}${path}`, {
        ...init, headers: { 'content-type': 'application/json', 'x-client-id': 'local-default', ...init?.headers },
      });
      if (!response.ok) throw Error(`${init?.method ?? 'GET'} ${path}: ${response.status}`);
      return response;
    };
    let current: any = null;
    let concurrentCore = 0;
    const requestSwitch = new WeakMap<object, string>();
    const samples = new Map<string, any>();
    const pendingResponses: Promise<void>[] = [];
    page.on('pageerror', error => report.pageErrors.push(error.message));
    page.on('request', request => {
      if (/\/api\/tasks\/[^/]+\/core(?:\?|$)/.test(request.url())) {
        concurrentCore++;
        report.maxConcurrentCoreRequests = Math.max(report.maxConcurrentCoreRequests, concurrentCore);
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
      if (request.url().includes('/api/')) report.requestErrors.push({
        path: new URL(request.url()).pathname, switchId: requestSwitch.get(request) ?? null,
        failure: request.failure()?.errorText ?? 'unknown',
      });
    });
    await page.route(/\/api\/tasks\/[^/]+\/core(?:\?|$)/, route => route.continue({ headers: {
      ...route.request().headers(), 'x-task-switch-trace': '1',
      'x-task-switch-id': current?.switchId ?? crypto.randomUUID(),
      'x-task-request-id': crypto.randomUUID(),
    } }));
    page.on('response', response => {
      if (response.url().includes('/api/') && response.status() >= 400)
        report.apiErrors.push({ path: new URL(response.url()).pathname, status: response.status() });
      if (/\/api\/tasks\/[^/]+\/details\//.test(response.url())) {
        const id = requestSwitch.get(response.request());
        const sample = id && samples.get(id);
        const etag = response.headers()['etag'];
        if (sample && etag) sample.resourceGenerations[new URL(response.url()).pathname] = etag;
      }
      if (!/\/api\/tasks\/[^/]+\/core(?:\?|$)/.test(response.url())) return;
      const id = requestSwitch.get(response.request());
      const sample = id && samples.get(id);
      if (!sample) return;
      pendingResponses.push((async () => {
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
        } catch { /* Missing body is a gate failure. */ }
        const serverTiming: Record<string, number> = {};
        for (const part of (headers['server-timing'] ?? '').split(',')) {
          const match = part.trim().match(/^([\w-]+);dur=([\d.]+)/);
          if (match) serverTiming[match[1]] = Number(match[2]);
        }
        const counter = (header: string) => /^\d+$/.test(headers[header] ?? '') ? Number(headers[header]) : null;
        sample.coreRequests.push({ status: response.status(), bytes, coreState, coreVersion,
          gitSpawns: counter('x-task-core-git-spawns'),
          workspaceScans: counter('x-task-core-workspace-scans'),
          requestId: headers['x-task-request-id'] ?? null,
          responseSwitchId: headers['x-task-switch-id'] ?? null, serverTiming });
      })());
    });
    await page.addInitScript(() => performance.setResourceTimingBufferSize(4096));
    const tasksCreated: string[] = [];
    try {
      for (const { id, state } of [
        ...taskClasses.flatMap(taskClass => taskClass.ids.map(id => ({ id, state: taskClass.state }))),
        { id: unrelatedId, state: '0-backlog' },
      ]) {
        try {
          await api('/api/tasks', { method: 'POST', body: JSON.stringify({
            id, title: id, watchPath, targetState: state, fixture: false,
            agent: 'codex', cliType: 'codex', promptMarkdown: `# ${id}\n\nIsolated switch budget fixture.`,
            requiresIntegration: false,
          }) });
        } catch (error) {
          throw Error(`Fixture task creation failed in ${state}: ${error}`);
        }
        tasksCreated.push(id);
      }
      for (const id of taskClasses.flatMap(taskClass => taskClass.ids))
        await expect.poll(async () => (await fetch(
          `${devBackend.baseUrl}/api/tasks/${encodeURIComponent(id)}/core?project=${encodeURIComponent(projectId)}`,
          { headers: { 'x-client-id': 'local-default' } })).status,
        { timeout: 30_000, intervals: [250, 500, 1000] }).toBe(200);
      await page.goto('/', { waitUntil: 'domcontentloaded', timeout: 60_000 });
      await showBoard(page);
      const scenarios = ['dirty-unrelated', 'own-task-mutation', 'git-unavailable', 'hung-refresh'] as const;
      for (const theme of ['light', 'dark']) for (const scenario of scenarios) {
        if (process.env.TASK_SWITCH_ONLY && scenario !== process.env.TASK_SWITCH_ONLY) continue;
        await showBoard(page);
        await page.evaluate(value => {
          document.documentElement.dataset['studioTheme'] = value;
          localStorage.setItem('atp.studio.theme', value);
        }, theme);
        const name = `${theme}/${scenario}`;
        const population: any = { warmups, warmupSamples: [], samples: [], cached: false,
          fixture: 'isolated dev-backend task repository', faultKind: scenario.startsWith('git')
            ? 'playwright-route-503' : scenario === 'hung-refresh'
              ? 'playwright-held-git-resource' : 'isolated-api-mutation',
          faultHits: 0, mutationCount: 0 };
        report.cohorts[name] = population;
        const held: (() => void)[] = [];
        const gitRoute = async (route: any) => {
          population.faultHits++;
          if (scenario === 'hung-refresh') await new Promise<void>(resolve => held.push(resolve));
          await route.fulfill({ status: 503, contentType: 'application/json', body: '{"state":"unavailable"}' })
            .catch(() => undefined);
        };
        if (scenario === 'git-unavailable' || scenario === 'hung-refresh')
          await page.route(/\/api\/tasks\/[^/]+\/details\/git(?:\?|$)/, gitRoute);
        for (let index = -warmups; index < count; index++) {
          const taskClass = taskClasses[(index + warmups) % taskClasses.length];
          const source = taskClass.ids[Math.floor((index + warmups) / taskClasses.length) % 2];
          const target = taskClass.ids.find(id => id !== source)!;
          const sample: any = { switchId: crypto.randomUUID().replaceAll('-', ''), target, projectId,
            input: taskClass.name === 'archived' ? 'click' : 'keydown',
            sampleClass: taskClass.name, expectedLane: taskClass.state,
            startedAtUtc: new Date().toISOString(),
            outcome: 'error', groupedReads: 0, coreRequests: [], resourceGenerations: {} };
          samples.set(sample.switchId, sample);
          try {
            await showBoard(page);
            if (taskClass.name === 'archived') {
              await expect(page.getByTestId('archive-filter-input')).toBeVisible();
              await page.getByTestId('archive-filter-input').fill(target);
              await expect(page.getByTestId('archive-row').filter({ hasText: target })).toBeVisible({ timeout: 30_000 });
            } else {
              await page.getByTestId('task-card').filter({ hasText: source }).first().click();
              await expect(page.getByTestId('task-core')).toHaveAttribute('data-core-id', source, { timeout: 30_000 });
              await expect(page.getByTestId('studio-task-pager-position')).toContainText('/ 2');
            }
            if (scenario === 'dirty-unrelated' || scenario === 'own-task-mutation') {
              const id = scenario === 'dirty-unrelated' ? unrelatedId : target;
              await api(`/api/tasks/${encodeURIComponent(id)}/title?watchPath=${encodeURIComponent(watchPath)}`,
                { method: 'PUT', body: JSON.stringify({ title: `${id} revision ${index + warmups}` }) });
              population.mutationCount++;
            } else {
              await page.evaluate(({ target, project }) => {
                void fetch(`/api/tasks/${encodeURIComponent(target)}/details/git?project=${encodeURIComponent(project)}`)
                  .catch(() => undefined);
              }, { target, project: projectId });
            }
            const previous = await page.evaluate(() => new URL(location.href).searchParams.get('job'));
            const before = await page.evaluate(() => performance.getEntriesByName('task-core-ready').length);
            await page.evaluate(input => window.addEventListener(input, event => {
              performance.mark('task-switch-budget-input');
              (window as any).__taskSwitchBudgetInputEpoch = performance.timeOrigin + event.timeStamp;
            }, { capture: true, once: true }), sample.input);
            current = sample;
            if (taskClass.name === 'archived') {
              await page.getByTestId('archive-row').filter({ hasText: target }).click();
            } else {
              const position = (await page.getByTestId('studio-task-pager-position').innerText()).trim();
              await page.keyboard.press(position.startsWith('1 ') ? 'j' : 'k');
            }
            await page.waitForFunction(({ before, previous, target }) => {
              const selected = new URL(location.href).searchParams.get('job');
              return selected !== previous && selected === target
                && performance.getEntriesByName('task-core-ready').length > before;
            }, { before, previous, target }, { polling: 'raf', timeout: 10_000 });
            await expect(page.getByTestId('studio-task')).toContainText(target);
            await expect(page.getByTestId('task-core-state')).toContainText(
              taskClass.name === 'active' ? 'Ready'
                : taskClass.name === 'review' ? 'Human review' : 'Archive');
            sample.classVerified = true;
            const paint = await page.evaluate(async () => {
              await new Promise<void>(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve())));
              return { ms: performance.now() - performance.getEntriesByName('task-switch-budget-input').at(-1)!.startTime,
                inputEpoch: (window as any).__taskSwitchBudgetInputEpoch as number };
            });
            sample.paintMs = paint.ms;
            sample.inputAtEpochMs = paint.inputEpoch;
            sample.coreFacts = true; // task-core-ready is emitted only after corePainted checks every section.
            if (await page.evaluate(() => document.documentElement.dataset['studioTheme']) !== theme)
              throw Error(`Expected ${theme} theme at core paint`);
            sample.outcome = 'ok';
            sample.resources = await page.evaluate(start => (performance.getEntriesByType('resource') as PerformanceResourceTiming[])
              .filter(entry => entry.name.includes('/api/') && entry.startTime >= start).slice(-128)
              .map(entry => ({ path: new URL(entry.name).pathname, startMs: entry.startTime,
                durationMs: entry.duration, transferBytes: entry.transferSize,
                decodedBytes: entry.decodedBodySize,
                ttfbMs: entry.responseStart - entry.requestStart })),
            await page.evaluate(() => performance.getEntriesByName('task-switch-budget-input').at(-1)!.startTime));
          } catch (error) {
            sample.outcome = 'error'; sample.error = String(error).slice(0, 300);
          } finally {
            held.splice(0).forEach(release => release());
            await Promise.allSettled(pendingResponses.splice(0));
            sample.finishedAtUtc = new Date().toISOString();
            current = null;
          }
          if (index >= 0) population.samples.push(sample);
          else population.warmupSamples.push(sample);
          if (index >= 0 && index < taskClasses.length && sample.outcome === 'ok') {
            const filename = `task-switch-isolated-${theme}-${scenario}-${taskClass.name}.png`;
            await page.screenshot({ path: resolve(output, '..', filename), timeout: 10_000,
              animations: 'disabled' });
            report.screenshots.push(filename);
          }
        }
        if (scenario === 'git-unavailable' || scenario === 'hung-refresh')
          await page.unroute(/\/api\/tasks\/[^/]+\/details\/git(?:\?|$)/, gitRoute);
      }
    } catch (error) {
      report.failure = String(error).slice(0, 400);
      throw error;
    } finally {
      for (const id of tasksCreated) {
        await fetch(`${devBackend.baseUrl}/api/tasks/${encodeURIComponent(id)}?watchPath=${encodeURIComponent(watchPath)}`,
          { method: 'DELETE', headers: { 'x-client-id': 'local-default' }, signal: AbortSignal.timeout(10_000) })
          .catch(() => undefined);
      }
      report.profile.finishedAt = new Date().toISOString();
      await mkdir(resolve(output, '..'), { recursive: true });
      await writeFile(output, JSON.stringify(report, null, 2) + '\n');
    }
    if (diagnostic && Object.values(report.cohorts).some((population: any) =>
      population.samples.some((sample: any) => sample.outcome !== 'ok')))
      throw Error(`Isolated diagnostic contains failed switches; inspect ${output}`);
  });
});
