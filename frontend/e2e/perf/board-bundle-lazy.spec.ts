import { test, expect, type Page } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { loadavg } from 'node:os';

const output = process.env.JOB_RESULTS_DIR;
const phase = process.env.BUNDLE_PHASE ?? 'after';
const project = 'Bundle Demo';
const watchPath = '/fixtures/bundle-demo';
const json = (body: unknown) => ({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });

function task(index: number) {
  const id = `BND-${index + 1}`;
  return {
    id, taskKey: `${watchPath}::${id}`, title: `Bundle demo card ${index + 1}`,
    state: '2-ready', order: index + 1, agent: 'codex', cliType: 'codex',
    createdAt: '2026-11-05T09:00:00Z', watchPath, projectName: project,
    folderPath: `${watchPath}/.orchestrator/tasks/2-ready/${id}`,
    lastActivity: '2026-11-05T09:00:00Z', sessionName: null, model: 'gpt-5.5',
    useOwnSession: null, lastUsage: null, execution: null, commit: null,
    commits: [], ownerClientId: 'local-default', tags: [],
  };
}

async function installFixture(page: Page, count: number): Promise<void> {
  const tasks = Array.from({ length: count }, (_, i) => task(i));
  const grouped = {
    backlog: [], preparation: [], orchestratorPrep: [], ready: tasks,
    progress: [], failedPickup: [], codeNotComplete: [], review: [],
    autoReview: [], humanReview: [], completed: [], archive: [],
  };
  await page.addInitScript(() => {
    localStorage.setItem('activeProjects', '[]');
    localStorage.setItem('atp.studio.panelState.v1', JSON.stringify({ active: 'explorer', visible: true }));
    const observer = new MutationObserver(() => {
      if (document.querySelector('[data-testid="task-card"]') && !(window as any).__boardFirstPaint) {
        requestAnimationFrame(() => requestAnimationFrame(() => {
          (window as any).__boardFirstPaint ??= performance.now();
        }));
      }
      if (document.querySelector('[data-testid="studio-task"]') && (window as any).__detailClickAt && !(window as any).__detailFirstPaint) {
        requestAnimationFrame(() => { (window as any).__detailFirstPaint ??= performance.now(); });
      }
    });
    observer.observe(document, { subtree: true, childList: true });
    document.addEventListener('pointerdown', event => {
      if ((event.target as Element).closest('[data-testid="task-card"]')) {
        (window as any).__detailClickAt = performance.now();
      }
    }, { capture: true });
  });
  await page.route('**/api/**', route => route.fulfill(json([])).catch(() => undefined));
  await page.route('**/api/v1/studio/auth/status', route => route.fulfill(json({
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  })));
  await page.route('**/api/v1/studio/board**', route => route.fulfill(json(grouped)));
  await page.route('**/api/v1/studio/runner/status**', route => route.fulfill(json({ projects: {} })));
  await page.route('**/api/v1/management/credential-inventory**', route => route.fulfill(json({
    generatedAt: '2026-11-05T09:00:00Z', items: [],
  })));
  await page.route('**/api/usage/cockpit**', route => route.fulfill(json({
    snapshotVersion: 1, workspaceId: 'ws-bnd', timeZone: 'UTC', weekStart: 1,
    generatedAt: '2026-11-05T09:00:00Z', calendar: {}, clis: [], cost: null,
    runs: [], slots: [], sources: {},
  })));
  await page.route('**/api/tasks/grouped**', route => route.fulfill(json(grouped)));
  await page.route('**/api/workspaces/*/quota/admission**', route => route.fulfill(json({
    workspace: { status: 'ok', codingAllowed: true, hostingAllowed: true, limited: false,
      message: null, overrideActive: false }, projects: [],
  })));
  await page.route('**/api/tasks', route => route.fulfill(json(tasks)));
  await page.route('**/api/watch-paths', route => route.fulfill(json([{ name: project, path: watchPath, rootPath: watchPath }])));
  await page.route(/\/api\/tasks\/(?!grouped)[^/?]+(\?|$)/, route => {
    const id = decodeURIComponent(new URL(route.request().url()).pathname.split('/').pop() ?? '');
    const found = tasks.find(t => t.id === id);
    return route.fulfill(found ? json({ info: found, promptMarkdown: '# Bundle demo', statusMarkdown: '', log: [],
      promptHistory: [], titleHistory: [], reviewEvidence: [], summaryState: { status: 'none' } })
      : { status: 404, body: '{}' });
  });
  await page.route('**/api/v1/projects**', route => route.fulfill(json([{ id: project, displayName: project,
    shortCode: 'BND', workspaceId: 'ws-bnd', storageLocation: watchPath, sortOrder: 0, archived: false }])));
  await page.route('**/api/v1/workspaces**', route => route.fulfill(json([{ id: 'ws-bnd', displayName: 'Demo Workspace',
    sortOrder: 0, isDefault: true, projects: [{ id: project, displayName: project, shortCode: 'BND',
      workspaceId: 'ws-bnd', storageLocation: watchPath, sortOrder: 0, archived: false }] }])));
}

test('cold board assets, detail open, and route reuse', async ({ browser }) => {
  test.setTimeout(120_000);
  const samples: { count: number; firstPaintMs: number; detailOpenMs: number; assetRequests: number }[] = [];
  for (const count of [20, 20, 20, 830, 830, 830]) {
    const context = await browser.newContext({ serviceWorkers: 'block' });
    const page = await context.newPage();
    await installFixture(page, count);
    const cdp = await context.newCDPSession(page);
    const events: { event: string; data: unknown }[] = [];
    for (const event of ['Network.requestWillBeSent', 'Network.responseReceived', 'Network.loadingFinished'] as const) {
      cdp.on(event, data => events.push({ event, data }));
    }
    await cdp.send('Network.enable');
    await page.goto('/');
    await page.waitForFunction(() => Boolean((window as any).__boardFirstPaint), undefined, { timeout: 30_000 });
    await expect(page.getByTestId('task-card').first()).toBeVisible();
    const firstPaintMs = await page.evaluate(() => (window as any).__boardFirstPaint as number);
    const boardPaintAt = await page.evaluate(() => performance.timeOrigin + (window as any).__boardFirstPaint as number);
    await page.waitForTimeout(1200);
    await page.getByTestId('task-card').first().click();
    await expect(page.getByTestId('studio-task')).toBeVisible({ timeout: 15_000 });
    await page.waitForFunction(() => Boolean((window as any).__detailFirstPaint));
    const detailOpenMs = await page.evaluate(() => (window as any).__detailFirstPaint - (window as any).__detailClickAt);
    const detailClickAt = await page.evaluate(() => performance.timeOrigin + (window as any).__detailClickAt as number);
    await page.waitForTimeout(500);
    const detailEvents = events.length;
    if (count === 20 && samples.length === 0) {
      // Switching tabs may reuse parsed modules; it must not download an asset twice.
      await page.getByTestId('studio-tab-board:__all__').click();
      await page.getByTestId('task-card').first().click();
      await expect(page.getByTestId('studio-task')).toBeVisible();
      await page.waitForTimeout(300);
    }
    const requests = events.filter(item => item.event === 'Network.requestWillBeSent')
      .map(item => (item.data as { request: { url: string } }).request.url)
      .filter(url => /\.(js|css)(\?|$)/.test(url));
    const duplicateAssets = requests.filter((url, index) => requests.indexOf(url) !== index);
    expect(duplicateAssets, `duplicate JS/CSS fetches after route switches: ${duplicateAssets.join(', ')}`).toEqual([]);
    samples.push({ count, firstPaintMs, detailOpenMs, assetRequests: requests.length });
    if (output && (samples.length === 1 || (count === 830 && samples.filter(sample => sample.count === 830).length === 1))) {
      mkdirSync(output, { recursive: true });
      const label = count === 830 ? '830' : '20';
      const timeline = events.slice(0, detailEvents);
      const requestPhase = new Map<string, 'cold' | 'idle' | 'detail'>();
      for (const item of timeline) {
        if (item.event !== 'Network.requestWillBeSent') continue;
        const data = item.data as { requestId: string; wallTime: number };
        const start = data.wallTime * 1000;
        requestPhase.set(data.requestId, start < boardPaintAt ? 'cold' : start < detailClickAt ? 'idle' : 'detail');
      }
      for (const segment of ['cold', 'idle', 'detail'] as const) {
        const segmentEvents = timeline.filter(item => requestPhase.get((item.data as { requestId: string }).requestId) === segment);
        writeFileSync(join(output, `chrome-network-${phase}-${label}-${segment}.json`), JSON.stringify(segmentEvents));
      }
      writeFileSync(join(output, `chrome-network-${phase}-${label}-markers.json`), JSON.stringify({ boardPaintAt, detailClickAt }));
    }
    await context.close();
  }
  if (output) writeFileSync(join(output, `browser-timing-${phase}.json`), JSON.stringify(samples, null, 2) + '\n');
  const median = [samples[0].firstPaintMs, samples[1].firstPaintMs, samples[2].firstPaintMs]
    .sort((a, b) => a - b)[1];
  const largeMedian = samples.filter(sample => sample.count === 830)
    .map(sample => sample.firstPaintMs).sort((a, b) => a - b)[1];
  console.log(`${phase}: 20-card median first paint ${median.toFixed(1)} ms; first detail ${samples[0].detailOpenMs} ms; 830-card median first paint ${largeMedian.toFixed(1)} ms`);
});

test('profile main-thread costs through first card insertion', async ({ browser }) => {
  test.skip(process.env.BUNDLE_PROFILE !== '1', 'Run only for a focused CPU profile.');
  const context = await browser.newContext({ serviceWorkers: 'block' });
  const page = await context.newPage();
  await installFixture(page, 20);
  const cdp = await context.newCDPSession(page);
  await cdp.send('Profiler.enable');
  await cdp.send('Profiler.start');
  await page.goto('/');
  await page.waitForFunction(() => Boolean(document.querySelector('[data-testid="task-card"]')));
  const { profile } = await cdp.send('Profiler.stop');
  if (output) writeFileSync(join(output, 'chrome-cpu-after-20-first-card.json'), JSON.stringify(profile));
  const nodes = new Map(profile.nodes.map(node => [node.id, node.callFrame]));
  const costs = new Map<string, number>();
  profile.samples?.forEach((id, index) => {
    const frame = nodes.get(id);
    if (!frame) return;
    const label = `${frame.functionName || '(anonymous)'} ${frame.url}:${frame.lineNumber + 1}`;
    costs.set(label, (costs.get(label) ?? 0) + (profile.timeDeltas?.[index] ?? 0) / 1000);
  });
  const top = [...costs].sort((a, b) => b[1] - a[1]).slice(0, 5);
  console.log(`Main-thread costs through first card insertion: ${JSON.stringify(top)}`);
  if (output) writeFileSync(join(output, 'chrome-cpu-after-20-top-five.json'), JSON.stringify(top, null, 2) + '\n');
  await context.close();
});

test('create task, chat, and workspace settings mount on first use', async ({ page }) => {
  const assetRequests: string[] = [];
  page.on('request', request => {
    if (/\.(js|css)(\?|$)/.test(request.url())) assetRequests.push(request.url());
  });
  await installFixture(page, 20);
  await page.goto('/');
  await expect(page.getByTestId('task-card').first()).toBeVisible();
  await page.getByTestId('cockpit-header-more').click();
  await expect(page.getByRole('menuitem', { name: 'Orchestrator configuration' })).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(page.locator('app-orchestrator-side-sheet')).toHaveCount(0);
  if (output && phase === 'after') await page.screenshot({ path: join(output, 'board-first-paint.png') });
  await page.getByTestId('studio-board-add-task').click();
  await expect(page.getByTestId('create-title')).toBeVisible();
  if (output && phase === 'after') await page.screenshot({ path: join(output, 'create-task-first-open.png') });
  await page.getByTestId('create-cancel').click();
  await page.getByTestId('studio-titlebar-chat').click();
  await expect(page.locator('app-orchestrator-side-sheet')).toBeVisible();
  if (output && phase === 'after') await page.screenshot({ path: join(output, 'chat-first-open.png') });
  await page.getByTestId('studio-titlebar-chat').click();
  await expect(page.locator('app-orchestrator-side-sheet')).not.toBeVisible();
  await page.getByTestId('studio-titlebar-settings').click();
  await expect(page.getByTestId('workspace-settings-inline')).toBeVisible();
  if (output && phase === 'after') await page.screenshot({ path: join(output, 'settings-first-open.png') });
  await page.getByTestId('studio-tab-board:__all__').click();
  await page.getByTestId('studio-titlebar-settings').click();
  await expect(page.getByTestId('workspace-settings-inline')).toBeVisible();
  expect(assetRequests.filter((url, index) => assetRequests.indexOf(url) !== index)).toEqual([]);
});

test('update center and diff2html styles load on first use, not with the cold board', async ({ page }) => {
  const assetRequests: string[] = [];
  page.on('request', request => {
    if (/\.(js|css)(\?|$)/.test(request.url())) assetRequests.push(request.url());
  });
  await installFixture(page, 20);
  await page.goto('/');
  await expect(page.getByTestId('task-card').first()).toBeVisible();
  await expect(page.getByTestId('status-bar')).toBeVisible();
  expect(assetRequests.filter(url => url.includes('diff2html'))).toEqual([]);
  await expect(page.locator('link[href="diff2html.css"]')).toHaveCount(0);
  await expect(page.locator('app-update-center')).toHaveCount(0);
  await expect(page.locator('app-auth-gate')).toHaveCount(0);

  await page.getByTestId('studio-titlebar-settings').click();
  await page.getByTestId('workspace-settings-rail-updates').click();
  await page.getByTestId('settings-update-open-center').click();
  await expect(page.getByTestId('update-center')).toBeVisible();
  await page.getByTestId('update-center-close').click();
  await expect(page.getByTestId('update-center')).toHaveCount(0);
});

test('interleaved baseline and candidate board timing', async ({ browser }) => {
  const baselineUrl = process.env.BUNDLE_BASELINE_URL;
  test.skip(!baselineUrl, 'Requires a saved baseline build on a second static port.');
  const pairedRuns = Number(process.env.BUNDLE_PAIRED_RUNS ?? 3);
  test.setTimeout(Math.max(240_000, pairedRuns * 60_000));
  const counts = pairedRuns > 3 ? [20] : [20, 830];
  const samples: { phase: 'before' | 'after'; count: number; pair: number; loadAverage1m: number; firstPaintMs: number; detailOpenMs: number }[] = [];
  for (const count of counts) {
    for (let run = 0; run < pairedRuns; run++) {
      const loadAverage1m = loadavg()[0];
      const order = run % 2 === 0 ? ['before', 'after'] as const : ['after', 'before'] as const;
      for (const current of order) {
        const context = await browser.newContext({ serviceWorkers: 'block' });
        const page = await context.newPage();
        await installFixture(page, count);
        await page.goto(current === 'before' ? baselineUrl! : '/', { timeout: 45_000 });
        await page.waitForFunction(() => Boolean((window as any).__boardFirstPaint), undefined, { timeout: 60_000 });
        const firstPaintMs = await page.evaluate(() => (window as any).__boardFirstPaint as number);
        await page.getByTestId('task-card').first().click();
        await page.waitForFunction(() => Boolean((window as any).__detailFirstPaint), undefined, { timeout: 60_000 });
        const detailOpenMs = await page.evaluate(() => (window as any).__detailFirstPaint - (window as any).__detailClickAt);
        samples.push({ phase: current, count, pair: run + 1, loadAverage1m, firstPaintMs, detailOpenMs });
        await context.close();
      }
    }
  }
  if (output) writeFileSync(join(output, `browser-timing-paired-${pairedRuns}.json`), JSON.stringify(samples, null, 2) + '\n');
  const median = (values: number[]) => {
    const sorted = values.sort((a, b) => a - b);
    const middle = Math.floor(sorted.length / 2);
    return sorted.length % 2 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
  };
  for (const count of counts) {
    for (const current of ['before', 'after'] as const) {
      const values = samples.filter(sample => sample.count === count && sample.phase === current);
      console.log(`interleaved ${current} ${count}: paint ${median(values.map(value => value.firstPaintMs)).toFixed(1)} ms, detail ${median(values.map(value => value.detailOpenMs)).toFixed(1)} ms`);
    }
    const pairs = Array.from({ length: pairedRuns }, (_, pair) => {
      const before = samples.find(sample => sample.count === count && sample.pair === pair + 1 && sample.phase === 'before')!;
      const after = samples.find(sample => sample.count === count && sample.pair === pair + 1 && sample.phase === 'after')!;
      return { paint: before.firstPaintMs - after.firstPaintMs, detail: before.detailOpenMs - after.detailOpenMs };
    });
    console.log(`interleaved ${count}: paired median paint improvement ${median(pairs.map(pair => pair.paint)).toFixed(1)} ms; detail improvement ${median(pairs.map(pair => pair.detail)).toFixed(1)} ms`);
  }
});
