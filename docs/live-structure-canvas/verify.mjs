// Browser check of the AGT-3955 Dossier: sections, decision points, images,
// overflow, prototype controls, keyboard, search, levels, layers, replay
// batching, reduced motion, specification fallback, render on demand and
// hidden-tab pause, in light and dark at 390 and 1440 px. Writes the real-scene
// measurements to assets/real-scene-metrics.json for the Budget table.
// Run from the repository root: node docs/live-structure-canvas/verify.mjs [outDir]
import { createRequire } from 'node:module';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { dirname, resolve } from 'node:path';
import { mkdirSync, readFileSync, existsSync, writeFileSync } from 'node:fs';

const dir = dirname(fileURLToPath(import.meta.url));
const require = createRequire(resolve(dir, '../../frontend/package.json'));
const { chromium } = require('playwright-core');
const out = process.argv[2] || process.env.JOB_RESULTS_DIR || resolve(dir, '.verify');
mkdirSync(out, { recursive: true });
const url = pathToFileURL(resolve(dir, 'index.html')).href;
const fail = m => { throw new Error(m); };
const checks = [];

// Static checks: workbench, schema, specifications, links to assets.
const wb = JSON.parse(readFileSync(resolve(dir, 'workbench.json'), 'utf8'));
for (const k of ['schemaVersion', 'id', 'title', 'summary', 'entrypoint', 'status', 'phase', 'updatedAt', 'sourceTaskKeys', 'implementationTasks']) if (!(k in wb)) fail('workbench lacks ' + k);
if (wb.status !== 'decision-pending' || wb.entrypoint !== 'index.html' || !wb.sourceTaskKeys.includes('AGT-3955')) fail('workbench header');
const ids = new Set();
for (const t of wb.implementationTasks) {
  if (!t.id || ids.has(t.id) || !t.title || !t.promptMarkdown) fail('task ' + t.id);
  for (const d of t.dependsOn || []) if (!ids.has(d)) fail(`${t.id} depends on ${d}, which is not earlier`);
  ids.add(t.id);
}
for (const f of ['AGT-3879', 'AGT-3949', 'AGT-3950', 'AGT-3954', 'AGT-3955']) if (!wb.relatedTo.includes(f)) fail('relatedTo lacks ' + f);
const html = readFileSync(resolve(dir, 'index.html'), 'utf8');
if (html.includes('—')) fail('em dash');
for (const m of html.matchAll(/(?:src|href)="(assets\/[^"]+|schema\/[^"]+|bench\/[^"]+|workbench\.json)"/g)) if (!existsSync(resolve(dir, m[1]))) fail('missing file ' + m[1]);
const Spec = require(resolve(dir, 'src/spec.js')), Model = require(resolve(dir, 'src/model.js'));
const facts = { nodes: new Set(Model.nodes.map(n => n.id)), links: new Set(Model.links.map(l => l.id)), warnings: new Set() };
for (const [k, v] of Object.entries(Model.specs)) if (Spec.validate(v, facts).ok !== (k !== 'invalid')) fail('specification ' + k);
checks.push(`static: workbench (${wb.implementationTasks.length} slices, ${wb.openDecisions.length} decisions, dependencies ordered), assets present, specifications validate, no em dash`);

const browser = await chromium.launch({ headless: true, ...(process.env.ATP_PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH ? { executablePath: process.env.ATP_PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH } : {}) });
let metrics = null;
try {
  for (const theme of ['light', 'dark']) {
    for (const width of [390, 1440]) {
      const reduce = width === 390;
      const page = await browser.newPage({ viewport: { width, height: 1000 }, colorScheme: theme, reducedMotion: reduce ? 'reduce' : 'no-preference' });
      const errors = [];
      page.on('pageerror', e => errors.push(e.message));
      page.on('console', m => { if (m.type() === 'error') errors.push(m.text()); });
      await page.goto(url, { waitUntil: 'load' });
      for (const s of ['alternatives', 'recommendation', 'evidence', 'open-decisions']) if (await page.locator(`[data-concept-section="${s}"]`).count() !== 1) fail('section ' + s);
      const points = await page.locator('[data-decision-id][data-decision-kind="single"]').count();
      if (points !== wb.openDecisions.length) fail(`decision points ${points}`);
      await page.evaluate(async () => { for (const i of document.images) { i.loading = 'eager'; } await Promise.all([...document.images].map(i => i.complete ? 0 : new Promise(r => { i.onload = i.onerror = r; }))); });
      const broken = await page.evaluate(() => [...document.images].filter(i => !i.complete || i.naturalWidth === 0).map(i => i.getAttribute('src')));
      if (broken.length) fail('broken images ' + broken.join(', '));
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth - innerWidth);
      if (overflow > 1) fail(`document overflows by ${overflow}px at ${width}`);
      await page.locator('#lsc').scrollIntoViewIfNeeded();
      const motion = await page.evaluate(() => LSC.state.motion);
      if (reduce && motion !== 'off') fail('reduced motion must switch motion off');
      if (!reduce && motion !== 'lively') fail('default motion');
      // Layers combine and toggle.
      if (await page.locator('[data-control="layers"] button').count() !== 7) fail('seven layers');
      for (const b of await page.locator('[data-control="layers"] button').all()) { await b.click(); await b.click(); }
      const pressed = await page.locator('[data-control="layers"] button[aria-pressed="true"]').count();
      if (pressed !== 3) fail('default layers ' + pressed);
      // Levels.
      for (const lv of [1, 2, 3, 4]) {
        await page.locator(`[data-control="level"] button[data-value="${lv}"]`).click();
        // The label follows the rendered frame; wait for it rather than a fixed time.
        await page.waitForFunction(lv => document.getElementById('lsc-level').textContent.startsWith('Level ' + lv), lv, { timeout: 5000 }).catch(() => {});
        const label = await page.locator('#lsc-level').innerText();
        if (!label.startsWith('Level ' + lv)) {
          const d = await page.evaluate(() => { const e = LSC.engine.st, r = document.getElementById('lsc-wrap').getBoundingClientRect(); return { k: e.k, w: e.w, h: e.h, off: e.offscreen, hidden: e.hidden, frames: e.frames, raf: e.raf, timer: e.timer, tween: !!e.tween, level: LSC.engine.level(), rect: [Math.round(r.top), Math.round(r.bottom)], scrollY: scrollY }; });
          fail(`level ${lv} shows "${label}" at ${theme} ${width}: ${JSON.stringify(d)}`);
        }
      }
      // Keyboard: focus, move, open, escape.
      await page.locator('#lsc-fit').click();
      await page.locator('#lsc-wrap').focus();
      const ad0 = await page.locator('#lsc-wrap').getAttribute('aria-activedescendant');
      if (!ad0) fail('focus sets an active node');
      await page.keyboard.press('ArrowDown');
      await page.keyboard.press('ArrowRight');
      const ad1 = await page.locator('#lsc-wrap').getAttribute('aria-activedescendant');
      if (!ad1 || ad1 === ad0) fail('arrow keys move focus');
      if (await page.locator('#' + ad1).count() !== 1) fail('active descendant is in the outline');
      await page.keyboard.press('Enter');
      const selected = ad1.replace('lsc-o-', '');
      if (await page.evaluate(() => LSC.state.selectedId) !== selected) fail('Enter selects');
      await page.keyboard.press('Escape');
      if (await page.evaluate(() => LSC.state.selectedId)) fail('Escape clears selection');
      // Search to focus.
      await page.locator('#lsc-wrap').focus();
      await page.keyboard.press('/');
      await page.keyboard.type('secret');
      await page.keyboard.press('Enter');
      if (await page.evaluate(() => LSC.state.selectedId) !== 'm-studio') fail('search focuses the Studio server');
      if (!(await page.locator('#lsc-detail').innerText()).includes('Secret store key')) fail('detail lists the secret store key');
      // Pointer selection on the canvas.
      await page.evaluate(() => { LSC.select(null); LSC.engine.fit(0); });
      await page.waitForTimeout(200);
      const pt = await page.evaluate(() => { const n = LSC.engine.st.scene.byId.get('c-api'); const r = LSC.engine.rep(n); const q = r.q; return [(q[0][0] + q[2][0]) / 2, (q[0][1] + q[2][1]) / 2]; });
      const box = await page.locator('#lsc-canvas').boundingBox();
      await page.mouse.click(box.x + pt[0], box.y + pt[1]);
      const hitId = await page.evaluate(() => LSC.state.selectedId);
      if (!hitId) fail('click selects a node');
      // Assessment: personas, provenance, fallback.
      await page.locator('[data-control="persona"] button[data-value="operations-warden"]').click();
      const prov = await page.locator('.lsc-prov').innerText();
      if (!/Assessed .* revision .* confidence/.test(prov)) fail('provenance ' + prov);
      if (!(await page.locator('.lsc-goal').innerText()).includes('1 of 6')) fail('backup goal 1 of 6');
      await page.locator('[data-action="invalid-spec"]').evaluate(b => b.click());
      if (!(await page.locator('.lsc-reject').isVisible())) fail('invalid specification falls back');
      if (await page.evaluate(() => LSC.state.spec) !== null) fail('no specification drawn after rejection');
      await page.locator('[data-control="persona"] button[data-value="facts"]').click();
      // Missing backup stays marked with the Backup layer off.
      const marked = await page.locator('#lsc-o-c-api > button').textContent();
      if (!/backup: 2 of 3 must-keep without copy/.test(marked)) fail('outline marks the missing backup: ' + marked);
      // Replay: seek to the outage, then batching.
      await page.evaluate(() => LSC.seek(25));
      if (!(await page.locator('#lsc-outline').textContent()).includes('Tunnel, down')) fail('outline shows the tunnel down');
      await page.evaluate(() => LSC.seek(0));
      await page.locator('[data-control="speed"] button[data-value="4"]').click();
      const t0 = Date.now();
      await page.locator('#lsc-play').click();
      await page.waitForFunction(() => !LSC.state.playing, null, { timeout: 30000 });
      const secs = (Date.now() - t0) / 1000;
      const counts = await page.evaluate(() => ({ received: LSC.state.received, updates: LSC.state.updates }));
      if (counts.updates > Math.ceil(secs) + 1) fail(`more than one visual update a second: ${counts.updates} in ${secs.toFixed(1)} s`);
      if (counts.received < 40) fail('replay delivered deltas');
      if (!reduce && (await page.locator('#lsc-log li').count()) === 0) fail('event log');
      // 2.5D toggle and back.
      await page.locator('[data-control="view"] button[data-value="iso"]').click();
      await page.waitForTimeout(reduce ? 100 : 900);
      if (await page.evaluate(() => LSC.engine.st.isoTarget) !== 1) fail('2.5D');
      await page.locator('[data-control="view"] button[data-value="flat"]').click();
      await page.waitForTimeout(reduce ? 100 : 900);
      // Render on demand: no frame while idle in Calm; no frame while hidden even in Lively.
      await page.evaluate(() => { LSC.setMotion('calm'); LSC.seek(0); LSC.engine.fit(0); });
      await page.waitForTimeout(1500);
      const f0 = await page.evaluate(() => LSC.engine.st.frames);
      await page.waitForTimeout(3000);
      let idleFrames = await page.evaluate(() => LSC.engine.st.frames) - f0;
      if (idleFrames > 0) {
        // A late transition may still finish; a render loop would keep drawing in a second window.
        await page.waitForTimeout(2000);
        const g0 = await page.evaluate(() => LSC.engine.st.frames);
        await page.waitForTimeout(3000);
        idleFrames = await page.evaluate(() => LSC.engine.st.frames) - g0;
      }
      if (idleFrames > 0) {
        const st = await page.evaluate(() => { const e = LSC.engine.st; return { tween: !!e.tween, eff: e.effects.map(x => x.kind + ':' + x.id), tok: e.tokens.length, iso: e.iso, isoT: e.isoTarget, flow: e.flowActive, motion: e.motion, inMotion: e.inMotion, playing: LSC.state.playing, dirty: e.staticDirty, w: e.w, h: e.h, easing: LSC.painter.easingKeys(), settle: e.settle, blits: e.blits, full: e.fullDraws }; });
        const stacks = await page.evaluate(() => new Promise(res => {
          const seen = {}, raf = window.requestAnimationFrame, st = window.setTimeout;
          window.requestAnimationFrame = fn => { const k = (new Error().stack || '').split('\n').slice(2, 6).map(x => x.trim().replace(/file:.*index.html:/, '')).join(' < '); seen[k] = (seen[k] || 0) + 1; return raf(fn); };
          st(() => { window.requestAnimationFrame = raf; res(seen); }, 1000);
        }));
        fail(`idle canvas drew ${idleFrames} frames at ${theme} ${width}: ${JSON.stringify(st)} ${JSON.stringify(stacks)}`);
      }
      await page.evaluate(() => { LSC.setMotion('lively'); });
      await page.evaluate(() => { Object.defineProperty(document, 'hidden', { configurable: true, get: () => true }); document.dispatchEvent(new Event('visibilitychange')); });
      const h0 = await page.evaluate(() => LSC.engine.st.frames);
      await page.waitForTimeout(3000);
      const hiddenFrames = await page.evaluate(() => LSC.engine.st.frames) - h0;
      if (hiddenFrames > 0) fail(`hidden tab drew ${hiddenFrames} frames`);
      await page.evaluate(() => { Object.defineProperty(document, 'hidden', { configurable: true, get: () => false }); document.dispatchEvent(new Event('visibilitychange')); });
      if (theme === 'light' && width === 1440) {
        // Real-scene measurements for the Budget table.
        const cdp = await page.context().newCDPSession(page);
        await cdp.send('Performance.enable');
        const task = async () => (await cdp.send('Performance.getMetrics')).metrics.find(m => m.name === 'TaskDuration').value;
        await page.evaluate(() => { LSC.setLayers(['perf', 'health', 'warnings']); LSC.engine.fit(0); });
        await page.waitForTimeout(800);
        const a0 = await page.evaluate(() => LSC.engine.st.frames), c0 = await task();
        await page.waitForTimeout(5000);
        const flowFps = (await page.evaluate(() => LSC.engine.st.frames) - a0) / 5, flowCpu = (await task() - c0) / 5 * 100;
        // Headless rAF is not capped at 60 Hz, so report the cost of a frame, not a rate.
        const pan = await page.evaluate(() => new Promise(res => {
          const e = LSC.engine, start = performance.now(), ms = []; let i = 0;
          const step = () => {
            if (performance.now() - start > 3000) { ms.sort((a, b) => a - b); return res({ p50: ms[Math.floor(ms.length / 2)], p95: ms[Math.floor(ms.length * 0.95)] }); }
            e.zoomAt(e.st.w / 2, e.st.h / 2, i++ % 60 < 30 ? 1.02 : 1 / 1.02); e.panBy(Math.sin(i / 9) * 4, 0);
            const t0 = performance.now(); e.renderNow(); ms.push(performance.now() - t0);
            requestAnimationFrame(step);
          };
          requestAnimationFrame(step);
        }));
        metrics = { measuredAt: new Date().toISOString(), scene: 'the installation of this Dossier, 1440 px, headless Chromium', panFrameMsP50: +pan.p50.toFixed(1), panFrameMsP95: +pan.p95.toFixed(1), idleFrames, hiddenFrames, idleSeconds: 3,
          flowFps: Math.round(flowFps), flowCpuPct: Math.round(flowCpu), deltas: counts.received, updates: counts.updates, replaySeconds: Math.round(secs * 4) };
      }
      if (errors.length) fail(errors.join('\n'));
      await page.screenshot({ path: resolve(out, `dossier-${width}-${theme}.png`), fullPage: true });
      checks.push(`${theme} ${width}: sections, ${points} decision points, images, no overflow, motion ${motion}, layers, levels 1-4, keyboard, search, click, provenance, fallback, missing backup marked, replay ${counts.received} deltas in ${counts.updates} updates over ${secs.toFixed(1)} s, 2.5D, idle ${idleFrames} frames, hidden ${hiddenFrames} frames, no page errors`);
      await page.close();
    }
  }
} finally { await browser.close(); }
if (metrics) writeFileSync(resolve(dir, 'assets/real-scene-metrics.json'), JSON.stringify(metrics, null, 2) + '\n');
console.log(checks.join('\n'));
if (metrics) console.log(JSON.stringify(metrics));
