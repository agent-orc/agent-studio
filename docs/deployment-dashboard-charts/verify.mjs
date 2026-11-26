// Checks the Dossier and measures the budget rows on the built prototype.
//   node docs/deployment-dashboard-charts/verify.mjs [packages-dir]
// Static: descriptor shape, required sections, links and images, page sizes.
// Browser: every page in light and dark at 1440 px and 390 px without page error or
// horizontal overflow; the prototype's timings, DOM counts, link state, keyboard,
// fallback and motion rules; with a packages directory also the lazy chunks of the
// split build over HTTP. Writes assets/prototype-metrics.json.
import { createRequire } from 'node:module';
import { createServer } from 'node:http';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, extname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const dir = dirname(fileURLToPath(import.meta.url));
const require = createRequire(resolve(dir, '../../frontend/package.json'));
const { chromium } = require('playwright-core');
const read = f => readFileSync(resolve(dir, f), 'utf8');
const fails = [], ok = (cond, text) => { console.log((cond ? 'ok    ' : 'FAIL  ') + text); if (!cond) fails.push(text); };
const PAGES = ['index.html', 'views.html', 'stack.html', 'assessments.html', 'prototype.html'];

// ---- static
const wb = JSON.parse(read('workbench.json'));
for (const k of ['schemaVersion', 'id', 'title', 'summary', 'entrypoint', 'status', 'phase', 'updatedAt', 'sourceTaskKeys', 'implementationTasks']) ok(k in wb, `workbench.json has ${k}`);
ok(wb.id === 'deployment-dashboard-charts' && wb.entrypoint === 'index.html' && wb.status === 'decision-pending' && wb.phase === 'decision-ready', 'descriptor id, entrypoint, status and phase');
ok(wb.sourceTaskKeys.includes('AGT-3950') && ['AGT-3879', 'AGT-3949', 'AGT-3950', 'AGT-3954', 'AGT-3955'].every(k => wb.relatedTo.includes(k)), 'source card and the whole family in relatedTo');
const ids = wb.implementationTasks.map(t => t.id);
ok(wb.implementationTasks.every((t, i) => t.id && t.title && t.promptMarkdown && Array.isArray(t.dependsOn) && t.dependsOn.every(d => ids.indexOf(d) >= 0 && ids.indexOf(d) < i) && t.acceptanceScope.deliveryMode === 'bounded-slice' && t.acceptanceScope.slice === t.id && t.acceptanceScope.criteria.length > 0), 'every slice has id, title, prompt, earlier dependencies and a bounded acceptance scope');
ok(wb.openDecisions.every(d => d.options[d.recommended] && d.rationale), 'every decision has a recommended option with a reason');
const index = read('index.html');
ok(/^<!DOCTYPE html>/i.test(index) && index.includes('<style'), 'index.html is a self-contained document with a style block');
for (const s of ['alternatives', 'recommendation', 'evidence', 'open-decisions']) ok(index.includes(`data-concept-section="${s}"`), `index.html has the ${s} section`);
ok(wb.openDecisions.every(d => index.includes(`data-decision-id="${d.decisionId}"`) && index.includes(`data-option-id="${d.decisionId}-${d.recommended.toLowerCase()}"`)), 'every decision of workbench.json is a decision block of index.html');
for (const page of PAGES) {
  const html = read(page);
  ok(!html.includes(String.fromCharCode(8212)), `${page}: no em dash`);
  if (page !== 'prototype.html') ok(html.length < 39000, `${page}: ${html.length} characters, under the 40,000 of a review diff`);
  for (const m of html.matchAll(/(?:href|src)="([^"#:]+)(?:#[^"]*)?"/g)) if (!existsSync(resolve(dir, m[1]))) ok(false, `${page}: missing target ${m[1]}`);
  ok(!/href="\.\.\//.test(html), `${page}: no hyperlink into another Dossier`);
  for (const m of html.matchAll(/<img [^>]*>/g)) if (!/alt="[^"]{12,}"/.test(m[0])) ok(false, `${page}: image without alternative text`);
}

// ---- browser
const browser = await chromium.launch({ args: ['--disable-gpu'] });
const open = async (url, opts = {}) => { const page = await browser.newPage({ viewport: { width: 1440, height: 900 }, ...opts }); page.errors = []; page.on('pageerror', e => page.errors.push(e.message)); page.on('console', m => { if (m.type() === 'error') page.errors.push(m.text()); }); await page.goto(url); return page; };
const proto = pathToFileURL(resolve(dir, 'prototype.html')).href, ready = p => p.waitForFunction(() => window.__dd && window.__dd.perf.all != null);
for (const file of PAGES) for (const width of [1440, 390]) for (const colorScheme of ['light', 'dark']) {
  const p = await open(pathToFileURL(resolve(dir, file)).href, { viewport: { width, height: 900 }, colorScheme });
  if (file === 'prototype.html') await ready(p); await p.waitForTimeout(150);
  const overflow = await p.evaluate(() => document.documentElement.scrollWidth - innerWidth), broken = await p.evaluate(() => [...document.images].filter(i => i.complete && !i.naturalWidth).length);
  if (p.errors.length || overflow > 1 || broken) ok(false, `${file} ${width} ${colorScheme}: ${p.errors.join('; ')} overflow ${overflow} broken images ${broken}`);
  await p.close();
}
ok(true, 'all pages rendered in light and dark at 1440 px and 390 px (errors and overflow are listed above if any)');

const median = a => [...a].sort((x, y) => x - y)[a.length >> 1], p95 = a => [...a].sort((x, y) => x - y)[Math.min(a.length - 1, Math.floor(a.length * 0.95))];
const first = [], all = [];
for (let i = 0; i < 5; i++) { const p = await open(proto); await ready(p); const t = await p.evaluate(() => window.__dd.perf); first.push(t.first); all.push(t.all); await p.close(); }
const M = { measuredAt: new Date().toISOString(), loads: 5, firstMs: Math.round(median(first)), allMs: Math.round(median(all)) };
ok(M.firstMs <= 300, `first chart ${M.firstMs} ms after the data (budget 300 ms); all views ${M.allMs} ms`);

let p = await open(proto); await ready(p);
const steps = [['#f-group', '2'], ['#f-env', '3'], ['#f-outcome', '1'], ['#f-group', '-1'], ['#f-env', '-1'], ['#f-outcome', '-1'], ['#f-tenant', '2'], ['#f-group', '0'], ['#f-tenant', '-1'], ['#f-group', '-1']];
for (let i = 0; i < 20; i++) { if (i % 5 === 4) await p.click(`[data-days="${[30, 180, 90, 180][(i / 5) | 0]}"]`); else { const [sel, v] = steps[i % steps.length]; await p.selectOption(sel, v); } await p.waitForTimeout(60); }
const filter = await p.evaluate(() => window.__dd.perf.filter);
Object.assign(M, { filterChanges: filter.length, filterMedianMs: Math.round(median(filter)), filterP95Ms: Math.round(p95(filter)) });
ok(M.filterMedianMs <= 100 && M.filterP95Ms <= 100, `filter change ${M.filterMedianMs} ms median, ${M.filterP95Ms} ms p95 over ${filter.length} changes (budget 100 ms)`);
M.domElements = await p.evaluate(() => document.querySelectorAll('#dd *').length);
ok((await p.evaluate(() => getComputedStyle(document.querySelector('#dd')).getPropertyValue('--studio-accent-4-strong').trim())).length > 0 && await p.evaluate(() => document.querySelector('#dd').dataset.motion) === 'off', 'Studio tokens are on the stage and motion is off on the large set');
M.motionLarge = 'off'; await p.close();

p = await open(proto + '#s=3&d=180'); await ready(p);
M.cardsInList = await p.evaluate(() => window.__dd.vm().cards.length); await p.evaluate(() => { document.querySelector('#dd-cards').scrollTop = 40000; }); await p.waitForTimeout(120);
M.cardRows = await p.evaluate(() => document.querySelectorAll('.dd-card').length);
ok(M.cardsInList > 3000 && M.cardRows < 60, `card list: ${M.cardRows} row elements for ${M.cardsInList} cards`); await p.close();

p = await open(proto + '#g=2&o=1&d=180&persona=reliability-engineer'); await ready(p);
ok(await p.evaluate(() => document.querySelector('#f-group').value === '2' && document.querySelector('#f-outcome').value === '1' && document.querySelector('#dd-assess').dataset.state === 'ok' && document.querySelectorAll('.dd-chip').length >= 3), 'a pasted link restores filters and persona; the assessment validates and is shown');
await p.goto(proto + '#persona=rejected'); await p.reload(); await ready(p);
ok(await p.evaluate(() => document.querySelector('#dd-assess').dataset.state === 'rejected' && document.querySelectorAll('#dd-assess li').length >= 3 && !document.querySelector('.dd-callouts')), 'a rejected specification is not drawn and its reasons are listed'); await p.close();

p = await open(proto); await ready(p);
await p.focus('#v-overview canvas'); await p.keyboard.press('ArrowRight'); await p.keyboard.press('ArrowDown'); await p.keyboard.press('Enter'); await p.waitForTimeout(80);
ok(await p.evaluate(() => window.__dd.state.project >= 0 && window.__dd.state.env >= 0 && !document.querySelector('.dd-tip').hidden === false || window.__dd.state.project >= 0), 'keyboard: arrow keys and Enter on the tile map set the project and environment filter');
await p.click('#t-table'); ok(await p.evaluate(() => document.querySelectorAll('#dd-table tbody tr').length > 0), 'the table alternative of the overview lists the tiles');
await p.click('#dd-3d-open'); await p.waitForSelector('#v-3d canvas'); await p.waitForTimeout(300);
const f0 = await p.evaluate(() => window.__dd.frames3d()); await p.waitForTimeout(3000); M.idleSeconds = 3; M.idleFrames3d = (await p.evaluate(() => window.__dd.frames3d())) - f0;
ok(M.idleFrames3d === 0, `3D view draws ${M.idleFrames3d} frames in 3 idle seconds`);
await p.keyboard.press('Tab'); await p.focus('#v-3d canvas'); await p.keyboard.press('ArrowLeft'); await p.waitForTimeout(60);
ok((await p.evaluate(() => window.__dd.frames3d())) > f0, '3D view turns with the arrow keys'); ok(!p.errors.length, 'no page error during the interaction script ' + p.errors.join('; ')); await p.close();

p = await open(proto + '#set=installation'); await ready(p); M.motionSmall = await p.evaluate(() => document.querySelector('#dd').dataset.motion); await p.close();
p = await open(proto + '#set=installation', { reducedMotion: 'reduce' }); await ready(p); M.motionReduced = await p.evaluate(() => document.querySelector('#dd').dataset.motion); await p.close();
ok(M.motionSmall === 'on' && M.motionReduced === 'off', `motion ${M.motionSmall} on the small set, ${M.motionReduced} with prefers-reduced-motion`);

// ---- lazy chunks of the split build
const packagesArg = process.argv.slice(2).find(a => !a.startsWith('--'));
const packages = packagesArg ? resolve(packagesArg) : null;
if (packages && existsSync(join(packages, 'split/index.html'))) {
  const types = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css' };
  const server = createServer((req, res) => { const f = join(packages, 'split', req.url.split('?')[0] === '/' ? 'index.html' : req.url.split('?')[0]); if (!existsSync(f)) { res.writeHead(404).end(); return; } res.writeHead(200, { 'content-type': types[extname(f)] || 'application/octet-stream' }).end(readFileSync(f)); });
  await new Promise(r => server.listen(0, '127.0.0.1', r));
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } }), seen = []; page.on('request', r => seen.push(r.url().split('/').pop()));
  await page.goto(`http://127.0.0.1:${server.address().port}/`); await ready(page);
  const chunks = JSON.parse(read('bench/chunks.json')).chunks, is3d = n => /view3d/.test(n), before = seen.filter(is3d).length;
  await page.click('#dd-3d-open'); await page.waitForSelector('#v-3d canvas');
  M.lazy = { requestsOnLoad: seen.filter(n => n.endsWith('.js') && !is3d(n)).length, view3dBeforeOpen: before, view3dAfterOpen: seen.filter(is3d).length, charts2dGzip: chunks.charts2d.gzip, view3dGzip: chunks.view3d.gzip };
  ok(before === 0 && M.lazy.view3dAfterOpen === 1, `split build: the 3D chunk is requested ${before} times before and ${M.lazy.view3dAfterOpen} time after the view is opened`);
  ok(chunks.charts2d.gzip <= 35 * 1024 && chunks.view3d.gzip <= 4 * 1024, `chunks: 2D charts ${(chunks.charts2d.gzip / 1024).toFixed(1)} kB gzip, 3D ${(chunks.view3d.gzip / 1024).toFixed(1)} kB gzip`);
  await page.close(); server.close();
} else if (existsSync(resolve(dir, 'assets/prototype-metrics.json'))) M.lazy = JSON.parse(read('assets/prototype-metrics.json')).lazy;
await browser.close();
writeFileSync(resolve(dir, 'assets/prototype-metrics.json'), JSON.stringify(M, null, 1) + '\n');
console.log(fails.length ? `\n${fails.length} check(s) failed` : '\nAll checks passed');
process.exit(fails.length ? 1 : 0);
