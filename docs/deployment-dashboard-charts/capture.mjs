// Captures every view of the prototype in light and dark at 1440 px and 390 px,
// plus the assessment, rejection, cross-filter and installation states.
//   node docs/deployment-dashboard-charts/capture.mjs     (after build.mjs; writes assets/)
import { createRequire } from 'node:module';
import { execFileSync } from 'node:child_process';
import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const dir = dirname(fileURLToPath(import.meta.url));
const require = createRequire(resolve(dir, '../../frontend/package.json'));
const { chromium } = require('playwright-core');
const assets = resolve(dir, 'assets'); mkdirSync(assets, { recursive: true });
const base = pathToFileURL(resolve(dir, 'prototype.html')).href;
const VIEWS = ['figures', 'overview', 'flow', 'timeline', '3d'];
// name, URL state, element, widths
const STATES = [
  ...VIEWS.map(v => ({ name: 'view-' + v, hash: v === 'flow' ? 's=2' : v === '3d' ? 'v3d=1' : '', sel: `section[data-view="${v}"]`, widths: [1440, 390] })),
  { name: 'dashboard', hash: '', sel: '#dd', widths: [1440, 390] },
  { name: 'assessment-warden', hash: 'persona=operations-warden', sel: '#dd', clip: 1500, widths: [1440] },
  { name: 'assessment-reliability', hash: 'persona=reliability-engineer', sel: '#dd', clip: 1500, widths: [1440] },
  { name: 'assessment-rejected', hash: 'persona=rejected', sel: '#dd', clip: 620, widths: [1440] },
  { name: 'crossfilter', hash: 'g=2&o=1&d=180&s=1', sel: '#dd', clip: 1700, widths: [1440], themes: ['light'] },
  { name: 'tenants', hash: 'split=1&e=3&t=2', sel: 'section[data-view="overview"]', widths: [1440], themes: ['light'] },
  { name: 'installation', hash: 'set=installation&persona=operations-warden&d=180', sel: '#dd', clip: 1500, widths: [1440], themes: ['light'] },
];
const browser = await chromium.launch({ args: ['--disable-gpu'] });
const frames = [], problems = [];
for (const st of STATES) for (const width of st.widths) for (const theme of st.themes || ['light', 'dark']) {
  const page = await browser.newPage({ viewport: { width, height: 900 }, deviceScaleFactor: width < 700 ? 2 : 1, colorScheme: theme, reducedMotion: 'reduce' });
  page.on('pageerror', e => problems.push(`${st.name} ${width} ${theme}: ${e.message}`));
  page.on('console', m => { if (m.type() === 'error') problems.push(`${st.name} ${width} ${theme}: ${m.text()}`); });
  await page.goto(base + '#' + st.hash);
  await page.waitForFunction(() => window.__dd && window.__dd.perf.all != null);
  if (st.hash.includes('v3d')) await page.waitForSelector('#v-3d canvas');
  await page.waitForTimeout(250);
  if (await page.evaluate(() => document.documentElement.scrollWidth > innerWidth + 1)) problems.push(`${st.name} ${width} ${theme}: horizontal overflow`);
  const file = `${st.name}-${theme}-${width}.png`, target = page.locator(st.sel).first();
  if (st.clip) { const b = await target.boundingBox(); await page.screenshot({ path: resolve(assets, file), fullPage: true, clip: { x: b.x, y: b.y, width: b.width, height: Math.min(b.height, st.clip) } }); }
  else await target.screenshot({ path: resolve(assets, file) });
  frames.push(file); await page.close();
}
await browser.close();
const git = a => execFileSync('git', a, { cwd: dir, encoding: 'utf8' }).trim();
writeFileSync(resolve(assets, 'capture-manifest.json'), JSON.stringify({ capturedAt: new Date().toISOString(), commit: git(['rev-parse', 'HEAD']), source: 'docs/deployment-dashboard-charts/prototype.html, built from src/ by build.mjs; data: the seeded synthetic set of src/data.js (40 projects, 4 environments, 6 tenants, 2,000 deploy runs, 5,000 cards) unless the name says installation', browser: 'headless Chromium, CPU raster', frames, problems }, null, 1) + '\n');
console.log(frames.length, 'captures;', problems.length ? 'PROBLEMS\n' + problems.join('\n') : 'no page errors, no overflow');
process.exit(problems.length ? 1 : 0);
