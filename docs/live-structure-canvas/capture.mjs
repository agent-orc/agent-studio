// Evidence captures for docs/live-structure-canvas (AGT-3955).
// 1. Today's Structure view: serves the prebuilt frontend of this checkout and
//    answers every API call from the role-named fixture below; no backend.
//    Prerequisite: npm --prefix frontend run build (skipped when absent).
// 2. The Dossier prototype: every layer in light and dark at 1440 and 390 px,
//    the four levels, 2D and 2.5D, a rejected specification, the tunnel-down
//    moment, a screen recording of the replay and a filmstrip of six of its moments.
// Run from the repository root:
//   node docs/live-structure-canvas/capture.mjs [--skip-product] [--skip-prototype] [--skip-video] [--skip-filmstrip]
import { createRequire } from 'node:module';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { dirname, resolve } from 'node:path';
import { spawn, execFileSync } from 'node:child_process';
import { writeFileSync, readFileSync, existsSync, mkdirSync, renameSync, rmSync, readdirSync } from 'node:fs';
import net from 'node:net';

const dir = dirname(fileURLToPath(import.meta.url));
const repo = resolve(dir, '../..');
const require = createRequire(resolve(repo, 'frontend/package.json'));
const { chromium } = require('playwright-core');
const assets = resolve(dir, 'assets');
mkdirSync(assets, { recursive: true });
const args = new Set(process.argv.slice(2));
const launch = () => chromium.launch({ headless: true, ...(process.env.ATP_PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH ? { executablePath: process.env.ATP_PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH } : {}) });
// The manifest keeps one group per capture kind, so a run that skips a kind keeps that
// kind's files, date and commit from the run that made them.
const manifestPath = resolve(assets, 'capture-manifest.json');
const previous = existsSync(manifestPath) ? JSON.parse(readFileSync(manifestPath, 'utf8')) : {};
const commit = execFileSync('git', ['-C', repo, 'rev-parse', 'HEAD']).toString().trim();
const groups = { ...(previous.groups || {}) };
let report = null;
const begin = kind => { report = groups[kind] = { capturedAt: new Date().toISOString(), commit, provenance: '', frames: [] }; };

// ---------- 1. today's Structure view ----------
const NOW = '2026-11-17T21:40:00.000Z';
const node = (id, kind, label, parentId, extra = {}) => ({ id, kind, label, parentId, unitId: null, environmentId: null, project: null,
  provenance: 'declared', sourceRef: 'role fixture', ...extra });
const up = (cpu, version = 'r413', expected = 'r413') => ({ difference: 'matched', health: 'up', observedAt: NOW, version, expectedVersion: expected, behind: version !== expected, cpuPercent: cpu });
const structure = {
  nodes: [
    node('b', 'boundary', 'AGT installation', null),
    node('cloud', 'location', 'Cloud', 'b'), node('home', 'location', 'Home lab', 'b'),
    node('studio', 'cloud-server', 'Studio server', 'cloud', { observation: up(46) }),
    ...[['edge', 'edge', 3], ['web', 'web', 2], ['api', 'api', 31], ['engine', 'engine', 6], ['runner-a', 'runner daemon A', 64], ['runner-b', 'runner daemon B', 22]]
      .map(([id, label, cpu]) => node(id, 'container', label, 'studio', { role: id.startsWith('runner') ? 'execution' : 'control', observation: up(cpu) })),
    node('lab', 'physical', 'Home-lab machine', 'home', { observation: up(18) }),
    node('vm', 'vm', 'Linux VM', 'lab', { observation: up(35) }),
    ...[1, 2, 3].map(i => node(`review-${i}`, 'container', `review host ${i}`, 'vm', { role: 'execution', observation: up([41, 12, 2][i - 1]) })),
    node('gate', 'container', 'gate host', 'vm', { role: 'verification', observation: up(52, 'r412', 'r413') }),
    node('forwarder', 'container', 'link forwarder', 'vm', { role: 'control', provenance: 'observed', observation: { ...up(1), difference: 'undeclared' } }),
    node('cdn', 'external', 'CDN proxy', null), node('github', 'external', 'GitHub', null),
    node('registry', 'external', 'Container registry', null), node('models', 'external', 'Model providers', null),
  ],
  links: [
    { from: 'cdn', to: 'edge', kind: 'call', label: 'CDN proxy', sourceRef: 'role fixture' },
    { from: 'forwarder', to: 'api', kind: 'tunnel', label: 'Tunnel', sourceRef: 'role fixture' },
    { from: 'runner-a', to: 'models', kind: 'call', label: 'Model calls', sourceRef: 'role fixture' },
    { from: 'api', to: 'github', kind: 'call', label: 'Git', sourceRef: 'role fixture' },
    { from: 'registry', to: 'gate', kind: 'artifact', label: 'Image pull', sourceRef: 'role fixture' },
  ],
};
async function captureProduct(browser) {
  const dist = resolve(repo, 'frontend/dist/frontend/browser');
  if (!existsSync(resolve(dist, 'index.html'))) { console.error('No prebuilt frontend; skipping the product capture.'); return; }
  begin('product');
  const port = await new Promise(ok => { const s = net.createServer().listen(0, '127.0.0.1', () => { const p = s.address().port; s.close(() => ok(p)); }); });
  const server = spawn(process.execPath, [resolve(repo, 'frontend/scripts/serve-prebuilt-frontend.mjs'), dist, String(port), 'http://127.0.0.1:9'], { stdio: 'ignore' });
  const base = `http://127.0.0.1:${port}`;
  try {
    for (let i = 0; i < 50; i++) { try { await fetch(base); break; } catch { await new Promise(r => setTimeout(r, 200)); } }
    for (const theme of ['light', 'dark']) {
      const context = await browser.newContext({ viewport: { width: 1600, height: 1000 }, reducedMotion: 'reduce', colorScheme: theme, timezoneId: 'Europe/Berlin' });
      await context.addInitScript(t => { try { localStorage.setItem('atp.studio.theme', t); } catch { /* storage blocked */ } }, theme);
      await context.route('**/api/**', route => {
        const p = new URL(route.request().url()).pathname;
        const json = body => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
        if (p.endsWith('/auth/status')) return json({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null });
        if (p === '/api/v1/operations/structure') return json(structure);
        if (p === '/api/v1/operations/structure/library') return json({ registries: [], consumers: [] });
        if (p === '/api/v1/projects') return json([]);
        if (p === '/api/v1/workspaces' || p === '/api/workspaces') return json([{ id: 'main', displayName: 'Main', sortOrder: 0, isDefault: true, color: null, createdAt: '2026-10-01T00:00:00Z', projects: [] }]);
        if (p.startsWith('/api/v1/studio/board')) return json({ backlog: [], preparation: [], orchestratorPrep: [], failedPickup: [], codeNotComplete: [], autoReview: [], review: [], completed: [], archive: [], ready: [], progress: [], humanReview: [], escalated: [], gitStateAt: null, stale: false });
        if (p === '/api/workbenches') return json({ projectName: null, count: 0, currentCount: 0, historyCount: 0, items: [] });
        if (p === '/api/v1/studio/runner/status') return json({ projects: {} });
        if (p === '/api/environment') return json({ isDev: false, devTools: {} });
        if (p === '/api/usage/cockpit') return json({ workspaceId: 'main', clis: [], cost: null });
        if (p === '/api/cli/quota') return json({ at: NOW, ttlSeconds: 600, ttlMs: 600000, snapshots: [] });
        if (p === '/api/cli/usage') return json({ at: NOW, sessions: [] });
        if (p === '/api/crash-recovery/pending') return json({ pending: [] });
        if (p === '/api/tasks/archive') return json({ items: [], total: 0, offset: 0, limit: 50 });
        if (p === '/api/runner/queue-starvation') return json({ active: false, waitingTaskCount: 0, availableSlots: 0, thresholdMinutes: 30, observedAt: NOW, oldestEnteredLaneAt: null, items: [] });
        if (p.includes('/quota/admission')) return json({ workspaceId: 'main', workspace: { projectId: null, project: null, status: 'ok', message: null, overrideActive: false, codingAllowed: true, hostingAllowed: true, limited: false }, projects: [] });
        if (process.env.CAPTURE_DEBUG) console.error('fallback', p);
        return json([]);
      });
      const page = await context.newPage();
      const errors = [];
      page.on('pageerror', e => errors.push(e.message));
      await page.clock.setFixedTime(NOW);
      await page.goto(`${base}/#/workspace/operations/structure?level=3&overlay=health`, { waitUntil: 'load' });
      await page.evaluate(t => { document.documentElement.dataset.studioTheme = t; }, theme);
      const view = page.getByTestId('structure-view');
      await view.waitFor({ state: 'visible', timeout: 30000 });
      await page.getByTestId('structure-canvas').waitFor({ state: 'visible', timeout: 15000 });
      await page.getByText('runner daemon A').first().waitFor({ timeout: 10000 });
      await page.evaluate(async () => { await document.fonts.ready; await new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r))); });
      const name = `today-structure-view-${theme}--mocked.png`;
      const box = await view.boundingBox();
      await page.screenshot({ path: resolve(assets, name), animations: 'disabled', clip: { x: box.x, y: box.y, width: box.width, height: Math.min(box.height, 860) } });
      report.frames.push(name);
      if (errors.length) throw new Error(errors.join('\n'));
      await context.close();
    }
    report.provenance = 'Prebuilt frontend of this commit at #/workspace/operations/structure; every API response from the role-named fixture in capture.mjs; no backend.';
  } finally { server.kill(); }
}

// ---------- 2. prototype ----------
const url = pathToFileURL(resolve(dir, 'prototype.html')).href;
async function proto(browser, theme, width, fn, opts = {}) {
  const context = await browser.newContext({ viewport: { width, height: 1000 }, colorScheme: theme, reducedMotion: opts.reduce === false ? 'no-preference' : 'reduce', ...(opts.video ? { recordVideo: opts.video } : {}) });
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', e => errors.push(e.message));
  await page.goto(url, { waitUntil: 'load' });
  await page.locator('#lsc').scrollIntoViewIfNeeded();
  await page.evaluate(() => document.fonts.ready);
  await page.waitForTimeout(300);
  await fn(page);
  if (errors.length) throw new Error(errors.join('\n'));
  await context.close();
  return page;
}
const shot = async (page, sel, name, wait = 900) => {
  // The canvas draws only while it is on screen: bring it there first, so the wait covers
  // the frames that follow and the level chip, which updates one frame after the camera.
  await page.locator(sel).scrollIntoViewIfNeeded();
  await page.waitForTimeout(wait);
  await page.evaluate(() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r))));
  await page.locator(sel).screenshot({ path: resolve(assets, name), animations: 'disabled' });
  report.frames.push(name);
};
const LAYERS = ['perf', 'storage', 'backup', 'warnings', 'health', 'version', 'drift'];
async function captureProto(browser) {
  begin('prototype');
  for (const theme of ['light', 'dark']) {
    for (const width of [1440, 390]) {
      await proto(browser, theme, width, async page => {
        await page.evaluate(() => { LSC.setMotion('off'); LSC.setPersona('facts'); });
        for (const id of LAYERS) {
          // On a phone the canvas opens at level 1; focus the node that carries the layer.
          const focus = { perf: 'c-runner-a', storage: 'm-studio', backup: 'c-api', warnings: 'c-gate', health: 'vm-lab', version: 'c-gate', drift: 'c-forwarder' }[id];
          await page.evaluate(([id, focus, phone]) => {
            LSC.seek(id === 'health' ? 25 : 0);
            LSC.setLayers([id]);
            LSC.engine.fit(0);
            if (phone) { LSC.focusNode(focus, true); LSC.select(null); LSC.engine.st.focusId = null; LSC.engine.invalidate(); setTimeout(() => LSC.engine.emit("camera"), 200); }
          }, [id, focus, width < 600]);
          await shot(page, '#lsc-wrap', `layer-${id}-${theme}-${width}.png`);
        }
        if (width !== 1440) return;
        await page.evaluate(() => { LSC.seek(0); LSC.setPersona('operations-warden'); });
        await page.locator('[data-action="invalid-spec"]').evaluate(b => b.click());
        await shot(page, '#lsc-assessment', `assessment-rejected-${theme}-1440.png`, 300);
        await page.evaluate(() => { LSC.setPersona('reliability-engineer'); LSC.setLayers(['perf', 'health', 'warnings']); LSC.seek(25); LSC.engine.fit(0); });
        await shot(page, '#lsc-wrap', `replay-tunnel-down-${theme}-1440.png`);
        if (theme !== 'light') return;
        await page.evaluate(() => { LSC.seek(0); LSC.setPersona('operations-warden'); LSC.setLayers(['perf', 'health', 'warnings']); });
        for (const lv of [1, 2, 3, 4]) {
          await page.evaluate(lv => { LSC.engine.fit(0); LSC.goLevel(lv); }, lv);
          await shot(page, '#lsc-wrap', `level-${lv}-light-1440.png`, 1200);
        }
        await page.evaluate(() => { LSC.select(null); LSC.engine.st.focusId = null; LSC.setPersona('facts'); LSC.setLayers(['perf', 'health']); LSC.engine.fit(0); });
        await shot(page, '#lsc-wrap', 'view-2d-light-1440.png');
        await page.evaluate(() => LSC.setIso(true));
        await shot(page, '#lsc-wrap', 'view-25d-light-1440.png', 1600);
      });
    }
  }
  report.provenance = 'This Dossier\'s prototype.html in headless Chromium; reduced motion for stills; replay seek positions as named.';
}

async function captureVideo(browser) {
  begin('video');
  const tmp = resolve(dir, '.video-tmp');
  rmSync(tmp, { recursive: true, force: true });
  await proto(browser, 'light', 1440, async page => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.evaluate(() => { const r = document.getElementById('lsc').getBoundingClientRect(); window.scrollBy(0, r.top - 8); });
    await page.evaluate(() => { LSC.setMotion('lively'); LSC.setPersona('reliability-engineer'); LSC.setLayers(['perf', 'health', 'warnings']); LSC.engine.fit(0); });
    await page.locator('[data-control="speed"] button[data-value="2"]').click();
    await page.waitForTimeout(1200);
    await page.locator('#lsc-play').click();
    await page.waitForTimeout(23500);
  }, { reduce: false, video: { dir: tmp, size: { width: 1280, height: 800 } } });
  const file = readdirSync(tmp).find(f => f.endsWith('.webm'));
  renameSync(resolve(tmp, file), resolve(assets, 'replay-recording.webm'));
  rmSync(tmp, { recursive: true, force: true });
  report.frames.push('replay-recording.webm');
  report.provenance = 'Replay of the recorded deltas at speed 2, light theme, 1440 by 900, recorded with Playwright and scaled to 1280 by 800.';
}

// Six moments of the live replay at speed 1 in one image: the recording as stills, for
// readers whose viewer does not play video. Each frame is taken once its deltas have
// been applied by the one-update-a-second batcher.
const MOMENTS = [
  [4.4, 'Card 101 finished coding: a token leaves runner daemon A for the gate host'],
  [9.6, 'Batch gate runs on the gate host; a deploy step runs on the web container'],
  [12.5, 'The deploy restarts the web container'],
  [19.0, 'Gate passed: the token moves on to review host 2'],
  [25.8, 'The tunnel drops: link broken, home-lab health not observed'],
  [34.3, 'Tunnel back; review passed and the token returns to the API'],
];
async function captureFilmstrip(browser) {
  begin('filmstrip');
  const frames = [];
  await proto(browser, 'light', 1440, async page => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.evaluate(() => { const r = document.getElementById('lsc').getBoundingClientRect(); window.scrollBy(0, r.top - 8); });
    await page.evaluate(() => { LSC.setMotion('lively'); LSC.setPersona('reliability-engineer'); LSC.setLayers(['perf', 'health', 'warnings']); LSC.engine.fit(0); });
    await page.waitForTimeout(1200);
    await page.locator('#lsc-play').click();
    for (const [t, caption] of MOMENTS) {
      await page.waitForFunction(t => LSC.state.clock >= t, t, { timeout: 60000, polling: 50 });
      const clock = await page.evaluate(() => LSC.state.clock);
      frames.push({ clock, caption, png: (await page.locator('#lsc-wrap').screenshot()).toString('base64') });
      if (process.env.CAPTURE_DEBUG) writeFileSync(resolve(process.env.CAPTURE_DEBUG, `moment-${frames.length}.png`), Buffer.from(frames.at(-1).png, 'base64'));
    }
  }, { reduce: false });
  const stamp = s => `0:${String(Math.round(s)).padStart(2, '0')}`;
  const context = await browser.newContext({ viewport: { width: 1440, height: 800 }, colorScheme: 'light' });
  const page = await context.newPage();
  await page.setContent(`<!doctype html><meta charset="utf-8"><style>
    body { margin: 0; padding: 16px; background: #fbfaf8; color: #1f2328; font: 15px/1.4 system-ui, sans-serif; }
    main { display: grid; grid-template-columns: repeat(2, 1fr); gap: 16px; }
    figure { margin: 0; } img { display: block; width: 100%; border: 1px solid #d8d4cc; border-radius: 8px; }
    figcaption { margin-top: 6px; } b { font-variant-numeric: tabular-nums; margin-right: 6px; }
  </style><main>${frames.map(f => `<figure><img src="data:image/png;base64,${f.png}"><figcaption><b>${stamp(f.clock)}</b>${f.caption}</figcaption></figure>`).join('')}</main>`);
  await page.evaluate(() => Promise.all([...document.images].map(i => i.decode())));
  await page.locator('body').screenshot({ path: resolve(assets, 'replay-filmstrip-light-1440.png') });
  await context.close();
  report.frames.push('replay-filmstrip-light-1440.png');
  report.moments = frames.map(f => ({ replaySecond: +f.clock.toFixed(1), caption: f.caption }));
  report.provenance = 'Six frames of the canvas during the live replay at speed 1, motion Lively, light theme, 1440 by 900, composed into one image with their captions.';
}

const browser = await launch();
try {
  if (!args.has('--skip-product')) await captureProduct(browser);
  if (!args.has('--skip-prototype')) await captureProto(browser);
  if (!args.has('--skip-video')) await captureVideo(browser);
  if (!args.has('--skip-filmstrip')) await captureFilmstrip(browser);
} finally { await browser.close(); }
writeFileSync(manifestPath, JSON.stringify({ groups }, null, 2) + '\n');
console.log(Object.entries(groups).map(([k, g]) => `${k}: ${g.frames.length} files, captured ${g.capturedAt.slice(0, 10)}`).join('\n'));
