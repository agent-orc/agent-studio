// Check of the AGT-3955 Dossier. Static: workbench, links and anchors between
// the pages, specifications, the captures, and what the concept review receives
// (review-input.mjs). Browser, in light and dark at 390 and 1440 px: every page
// (sections, decision points, images, overflow, page errors) and the prototype
// (controls, keyboard, search, levels, layers, replay batching, reduced motion,
// specification fallback, render on demand, hidden-tab pause). Writes the
// real-scene measurements to assets/real-scene-metrics.json for the Budget table.
// Run from the repository root: node docs/live-structure-canvas/verify.mjs [outDir] [--no-metrics]
// --no-metrics checks without rewriting the measurements, so the pages stay as built.
import { createRequire } from 'node:module';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { dirname, resolve } from 'node:path';
import { mkdirSync, readFileSync, existsSync, writeFileSync } from 'node:fs';
import { reviewSnapshot, reviewDiff, LIMITS } from './review-input.mjs';

const dir = dirname(fileURLToPath(import.meta.url));
const require = createRequire(resolve(dir, '../../frontend/package.json'));
const { chromium } = require('playwright-core');
const argv = process.argv.slice(2).filter(a => !a.startsWith('--'));
const keepMetrics = process.argv.includes('--no-metrics');
const out = argv[0] || process.env.JOB_RESULTS_DIR || resolve(dir, '.verify');
mkdirSync(out, { recursive: true });
const fail = m => { throw new Error(m); };
const checks = [];

// Static checks.
const wb = JSON.parse(readFileSync(resolve(dir, 'workbench.json'), 'utf8'));
for (const k of ['schemaVersion', 'id', 'title', 'summary', 'entrypoint', 'status', 'phase', 'updatedAt', 'sourceTaskKeys', 'implementationTasks']) if (!(k in wb)) fail('workbench lacks ' + k);
if (wb.status !== 'decision-pending' || wb.entrypoint !== 'index.html' || !wb.sourceTaskKeys.includes('AGT-3955')) fail('workbench header');
const ids = new Set();
for (const t of wb.implementationTasks) {
  if (!t.id || ids.has(t.id) || !t.title || !t.promptMarkdown) fail('task ' + t.id);
  for (const d of t.dependsOn || []) if (!ids.has(d)) fail(`${t.id} depends on ${d}, which is not earlier`);
  // The descriptor model reads acceptanceScope as an object; a string makes workbench.json invalid.
  const s = t.acceptanceScope;
  if (!s || s.deliveryMode !== 'bounded-slice' || s.slice !== t.id || !Array.isArray(s.criteria) || !s.criteria.length) fail(`${t.id}: acceptanceScope must be { deliveryMode: "bounded-slice", slice, criteria[] }`);
  ids.add(t.id);
}
for (const d of wb.openDecisions) if (!d.options[d.recommended]) fail(`${d.id} has no recommended option`);
for (const f of ['AGT-3879', 'AGT-3949', 'AGT-3950', 'AGT-3954', 'AGT-3955']) if (!wb.relatedTo.includes(f)) fail('relatedTo lacks ' + f);
const pages = Object.fromEntries(wb.pages.map(f => [f, readFileSync(resolve(dir, f), 'utf8')]));
let links = 0;
for (const [file, html] of Object.entries(pages)) {
  if (html.includes('—')) fail(file + ': em dash');
  if (/<!--g:[A-Z_]+--><!--\/g-->/.test(html)) fail(file + ': empty generated region; run build.mjs');
  if (/\[object Object\]|>(undefined|null|NaN)</.test(html)) fail(file + ': a value was printed as an object, undefined, null or NaN');
  for (const m of html.matchAll(/(?:src|href)="([^"#:]*)(?:#([^"]*))?"/g)) {
    const target = m[1] || file;
    if (!existsSync(resolve(dir, target))) fail(`${file}: missing file ${target}`);
    if (m[2] && pages[target] && !pages[target].includes(`id="${m[2]}"`)) fail(`${file}: no anchor ${target}#${m[2]}`);
    links++;
  }
}
const Spec = require(resolve(dir, 'src/spec.js')), Model = require(resolve(dir, 'src/model.js'));
const facts = { nodes: new Set(Model.nodes.map(n => n.id)), links: new Set(Model.links.map(l => l.id)), warnings: new Set() };
for (const [k, v] of Object.entries(Model.specs)) if (Spec.validate(v, facts).ok !== (k !== 'invalid')) fail('specification ' + k);
for (const l of ['perf', 'storage', 'backup', 'warnings', 'health', 'version', 'drift']) for (const t of ['light', 'dark']) for (const w of [1440, 390]) {
  const f = `layer-${l}-${t}-${w}.png`;
  if (!existsSync(resolve(dir, 'assets', f)) || !pages['layers.html'].includes(`src="assets/${f}"`) || !pages['live.html'].includes(`<code>${f}</code>`)) fail('layer capture not shown and listed: ' + f);
}
checks.push(`static: workbench (${wb.implementationTasks.length} slices with bounded acceptance scope and ordered dependencies, ${wb.openDecisions.length} decisions each with a recommendation), ${links} links and anchors resolve, specifications validate, 28 layer captures shown and listed, no em dash`);
// What the concept review receives: nothing a reviewer must read may be missing or cut.
const root = resolve(dir, '../..'), own = 'docs/live-structure-canvas/';
const snap = reviewSnapshot(root, own + 'index.html'), diff = reviewDiff(root, own.slice(0, -1));
const foreign = snap.files.filter(f => !f.path.startsWith(own)).map(f => f.path + ' is outside the Dossier');
if (snap.problems.length || diff.problems.length || foreign.length) fail('review input: ' + [...snap.problems, ...diff.problems, ...foreign].join(' | '));
for (const f of [...wb.pages, 'workbench.json']) if (!snap.files.some(x => x.kind === 'text' && x.path === own + f)) fail('review snapshot lacks ' + f);
const cut = diff.files.filter(f => f.kept < f.characters).map(f => f.path.slice(own.length));
checks.push(`review input: diff ${diff.changedFiles} of ${LIMITS.diffFiles} files, ${diff.lines} of ${LIMITS.diffLines} lines, ${diff.characters} of ${LIMITS.diffCharacters} characters, cut: ${cut.join(', ') || 'none'} (generated); snapshot ${snap.inspected} of ${LIMITS.files} files, ${snap.textCharacters} of ${LIMITS.totalCharacters} characters; nothing outside the Dossier`);

async function checkPage(page, file, width) {
  if (file === 'index.html') {
    for (const s of ['alternatives', 'recommendation', 'evidence', 'open-decisions']) if (await page.locator(`[data-concept-section="${s}"]`).count() !== 1) fail('section ' + s);
    const points = await page.locator('[data-decision-id][data-decision-kind="single"]').count();
    if (points !== wb.openDecisions.length) fail(`decision points ${points}`);
    if (await page.locator('[data-option-id]').count() !== wb.openDecisions.reduce((n, d) => n + Object.keys(d.options).length, 0)) fail('decision options');
  }
  await page.evaluate(async () => { for (const i of document.images) { i.loading = 'eager'; } await Promise.all([...document.images].map(i => i.complete ? 0 : new Promise(r => { i.onload = i.onerror = r; }))); });
  const broken = await page.evaluate(() => [...document.images].filter(i => !i.complete || i.naturalWidth === 0).map(i => i.getAttribute('src')));
  if (broken.length) fail(`${file}: broken images ${broken.join(', ')}`);
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - innerWidth);
  if (overflow > 1) fail(`${file} overflows by ${overflow}px at ${width}`);
}

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
      for (const file of wb.pages.filter(f => f !== 'prototype.html')) {
        await page.goto(pathToFileURL(resolve(dir, file)).href, { waitUntil: 'load' });
        await checkPage(page, file, width);
        await page.screenshot({ path: resolve(out, `page-${file.replace('.html', '')}-${width}-${theme}.png`), fullPage: true });
      }
      await page.goto(pathToFileURL(resolve(dir, 'prototype.html')).href, { waitUntil: 'load' });
      await checkPage(page, 'prototype.html', width);
      await page.locator('#lsc').scrollIntoViewIfNeeded();
      const motion = await page.evaluate(() => LSC.state.motion);
      if (reduce && motion !== 'off') fail('reduced motion must switch motion off');
      if (!reduce && motion !== 'lively') fail('default motion');
      // Layers combine and toggle.
      if (await page.locator('[data-control="layers"] button').count() !== 7) fail('seven layers');
      for (const b of await page.locator('[data-control="layers"] button').all()) { await b.click(); await b.click(); }
      const pressed = await page.locator('[data-control="layers"] button[aria-pressed="true"]').count();
      if (pressed !== 3) fail('default layers ' + pressed);
      // Levels. The label follows the rendered frame; wait for it rather than a fixed time.
      for (const lv of [1, 2, 3, 4]) {
        await page.locator(`[data-control="level"] button[data-value="${lv}"]`).click();
        await page.waitForFunction(lv => document.getElementById('lsc-level').textContent.startsWith('Level ' + lv), lv, { timeout: 5000 }).catch(() => {});
        const label = await page.locator('#lsc-level').innerText();
        if (!label.startsWith('Level ' + lv)) fail(`level ${lv} shows "${label}" at ${theme} ${width}`);
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
      if (await page.evaluate(() => LSC.state.selectedId) !== ad1.replace('lsc-o-', '')) fail('Enter selects');
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
      const pt = await page.evaluate(() => { const q = LSC.engine.rep(LSC.engine.st.scene.byId.get('c-api')).q; return [(q[0][0] + q[2][0]) / 2, (q[0][1] + q[2][1]) / 2]; });
      const box = await page.locator('#lsc-canvas').boundingBox();
      await page.mouse.click(box.x + pt[0], box.y + pt[1]);
      if (!(await page.evaluate(() => LSC.state.selectedId))) fail('click selects a node');
      // Assessment: personas, provenance, fallback.
      await page.locator('[data-control="persona"] button[data-value="operations-warden"]').click();
      const prov = await page.locator('.lsc-prov').innerText();
      if (!/Assessed .* revision .* confidence/.test(prov)) fail('provenance ' + prov);
      if (!(await page.locator('.lsc-goal').innerText()).includes('1 of 6')) fail('backup goal 1 of 6');
      await page.locator('[data-action="invalid-spec"]').evaluate(b => b.click());
      if (!(await page.locator('.lsc-reject').isVisible())) fail('invalid specification falls back');
      if (await page.evaluate(() => LSC.state.spec) !== null) fail('no specification drawn after rejection');
      await page.locator('[data-control="persona"] button[data-value="facts"]').click();
      if (/\bnull\b|undefined|\[object/.test(await page.locator('.lsc-side').innerText())) fail('a panel prints null, undefined or an object');
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
      const framesIn = async ms => { const f0 = await page.evaluate(() => LSC.engine.st.frames); await page.waitForTimeout(ms); return await page.evaluate(() => LSC.engine.st.frames) - f0; };
      await page.evaluate(() => { LSC.setMotion('calm'); LSC.seek(0); LSC.engine.fit(0); });
      await page.waitForTimeout(1500);
      let idleFrames = await framesIn(3000);
      // A late transition may still finish; a render loop would keep drawing in a second window.
      if (idleFrames > 0) { await page.waitForTimeout(2000); idleFrames = await framesIn(3000); }
      if (idleFrames > 0) fail(`idle canvas drew ${idleFrames} frames at ${theme} ${width}`);
      await page.evaluate(() => { LSC.setMotion('lively'); });
      const setHidden = hidden => page.evaluate(h => { Object.defineProperty(document, 'hidden', { configurable: true, get: () => h }); document.dispatchEvent(new Event('visibilitychange')); }, hidden);
      await setHidden(true);
      const hiddenFrames = await framesIn(3000);
      if (hiddenFrames > 0) fail(`hidden tab drew ${hiddenFrames} frames`);
      await setHidden(false);
      if (theme === 'light' && width === 1440) {
        // Real-scene measurements for the Budget table.
        const cdp = await page.context().newCDPSession(page);
        await cdp.send('Performance.enable');
        const task = async () => (await cdp.send('Performance.getMetrics')).metrics.find(m => m.name === 'TaskDuration').value;
        await page.evaluate(() => { LSC.setLayers(['perf', 'health', 'warnings']); LSC.engine.fit(0); });
        await page.waitForTimeout(800);
        const c0 = await task();
        const flowFps = (await framesIn(5000)) / 5, flowCpu = (await task() - c0) / 5 * 100;
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
      await page.evaluate(() => { LSC.setLayers(['perf', 'health', 'warnings']); LSC.seek(0); LSC.engine.fit(0); scrollTo(0, 0); });
      await page.waitForTimeout(400);
      await page.screenshot({ path: resolve(out, `page-prototype-${width}-${theme}.png`), fullPage: true });
      checks.push(`${theme} ${width}: ${wb.pages.length} pages without overflow, broken image or page error; index has its four sections and ${wb.openDecisions.length} decision points; prototype: motion ${motion}, layers, levels 1-4, keyboard, search, click, provenance, fallback, missing backup marked, replay ${counts.received} deltas in ${counts.updates} updates over ${secs.toFixed(1)} s, 2.5D, idle ${idleFrames} frames, hidden ${hiddenFrames} frames`);
      await page.close();
    }
  }
} finally { await browser.close(); }
if (metrics && !keepMetrics) writeFileSync(resolve(dir, 'assets/real-scene-metrics.json'), JSON.stringify(metrics, null, 2) + '\n');
console.log(checks.join('\n'));
if (metrics) console.log(JSON.stringify(metrics));
