// Framework benchmark of the deployment dashboard Dossier (AGT-3950). From the repository root:
//   node docs/deployment-dashboard-charts/bench/run.mjs <packages-dir> [--only <candidate>] [--runs 5]
// <packages-dir> holds node_modules with the pinned candidates, installed outside the repository:
//   npm install --prefix <dir> --ignore-scripts echarts@6.1.0 echarts-gl@2.1.0 uplot@1.6.32 chart.js@4.6.2 \
//     chartjs-chart-matrix@3.1.0 chartjs-chart-sankey@0.24.1 chartjs-plugin-zoom@3.0.2 three@0.187.0
// Writes bench/results.json (with --only it replaces that candidate and keeps the rest) and one screenshot
// a candidate under <packages-dir>/shots/. Needs the frontend dev dependencies (esbuild, playwright-core).
import { createRequire } from 'node:module';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { dirname, resolve, join } from 'node:path';
import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, existsSync, readdirSync } from 'node:fs';
import { tmpdir, cpus } from 'node:os';
import { gzipSync, brotliCompressSync } from 'node:zlib';
import { studioTokens } from '../tokens.mjs';

const dir = dirname(fileURLToPath(import.meta.url));
const require = createRequire(resolve(dir, '../../../frontend/package.json'));
const esbuild = require('esbuild');
const { chromium } = require('playwright-core');
const argv = process.argv.slice(2), flag = n => { const i = argv.indexOf(n); return i >= 0 ? argv[i + 1] : null; };
const pkgs = resolve(argv.find(a => !a.startsWith('--') && a !== flag('--only') && a !== flag('--runs')) || process.env.DDC_BENCH_PACKAGES || '');
if (!existsSync(join(pkgs, 'node_modules'))) throw new Error('Pass the packages directory (it must hold node_modules)');
const ONLY = flag('--only'), RUNS = +(flag('--runs') || 5);
const nodePaths = [join(pkgs, 'node_modules')];
const out = mkdtempSync(join(tmpdir(), 'ddc-bench-')), shots = join(pkgs, 'shots');
mkdirSync(shots, { recursive: true });
const version = p => JSON.parse(readFileSync(join(pkgs, 'node_modules', p, 'package.json'), 'utf8')).version;
const launch = process.env.ATP_PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH ? { executablePath: process.env.ATP_PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH } : {};

const C2D = { echarts: './adapter-echarts.js', 'uplot-own': '../src/charts.js', chartjs: './adapter-chartjs.js' };
const C3D = { three: './view3d-three.js', 'echarts-gl': './view3d-echartsgl.js', 'canvas2d-own': '../src/view3d.js' };
// Lazy chunk cost: only what a product chunk would import (adapter plus library, no harness).
const sizeEntries = {
  ...Object.fromEntries(Object.entries(C2D).map(([k, f]) => [k, `import { mount } from '${f}'; globalThis.x = mount;`])),
  ...Object.fromEntries(Object.entries(C3D).map(([k, f]) => [k, `import { mount3d } from '${f}'; globalThis.x = mount3d;`])),
  'echarts+echarts-gl': `import { mount } from './adapter-echarts.js'; import { mount3d } from './view3d-echartsgl.js'; globalThis.x = [mount, mount3d];`,
  // The modular echarts-gl entry points, to record whether they bundle against echarts 6 at all.
  'echarts-gl-modular-import': `import * as e from 'echarts/core'; import { Bar3DChart } from 'echarts-gl/charts'; import { Grid3DComponent } from 'echarts-gl/components'; e.use([Bar3DChart, Grid3DComponent]); globalThis.x = e;`,
  'uplot-library-only': `import u from 'uplot'; import 'uplot/dist/uPlot.min.css'; globalThis.x = u;`,
  'three-library-only': `import { WebGLRenderer, Scene, OrthographicCamera, InstancedMesh, BoxGeometry, MeshLambertMaterial, DirectionalLight, HemisphereLight, Object3D, Color, Group } from 'three'; globalThis.x = [WebGLRenderer, Scene, OrthographicCamera, InstancedMesh, BoxGeometry, MeshLambertMaterial, DirectionalLight, HemisphereLight, Object3D, Color, Group];`,
};
const entryFile = name => C2D[name] || C3D[name];

async function bundle(name, contents) {
  const r = await esbuild.build({ stdin: { contents, resolveDir: dir, loader: 'js' }, bundle: true, minify: true, format: 'iife', write: false, nodePaths, outdir: join(out, name),
    loader: { '.css': 'css' }, target: 'es2022', logLevel: 'silent', legalComments: 'none' });
  const pick = ext => Buffer.concat(r.outputFiles.filter(f => f.path.endsWith(ext)).map(f => Buffer.from(f.contents)));
  const js = pick('.js'), css = pick('.css'), buf = Buffer.concat([js, css]);
  return { js: js.toString(), css: css.toString(), warnings: r.warnings.map(w => w.text).slice(0, 5),
    size: { rawKb: +(buf.length / 1024).toFixed(1), gzipKb: +(gzipSync(buf, { level: 9 }).length / 1024).toFixed(1), brotliKb: +(brotliCompressSync(buf).length / 1024).toFixed(1) } };
}

function procCpuSeconds(rootPid) {
  // CPU time of the whole browser process tree (renderer, GPU and browser processes).
  const kids = new Map();
  for (const p of readdirSync('/proc').filter(x => /^\d+$/.test(x))) {
    try { const st = readFileSync(`/proc/${p}/stat`, 'utf8'); const f = st.slice(st.lastIndexOf(')') + 2).split(' '); kids.set(+p, { ppid: +f[1], cpu: (+f[11] + +f[12]) }); } catch { /* gone */ }
  }
  let total = 0; const stack = [rootPid];
  while (stack.length) { const p = stack.pop(); const k = kids.get(p); if (k) total += k.cpu; for (const [c, v] of kids) if (v.ppid === p) stack.push(c); }
  return total / 100; // clock ticks to seconds (USER_HZ 100)
}

// The harness page wears the Studio's light tokens, resolved from the product's token files.
const tokenCss = Object.entries(studioTokens().light).map(([k, v]) => `${k}:${v}`).join(';');
const pageHtml = b => `<!doctype html><meta charset="utf-8"><style>body{margin:0;background:#fff;font-family:system-ui}#stage{${tokenCss};width:1400px;margin:0 auto;padding:8px 0}
.figs{display:grid;grid-template-columns:repeat(4,1fr);gap:16px}.fig{height:48px;position:relative}#overview{height:780px}#flow{height:320px}#timeline{height:300px}#brush{height:90px}
#overview,#flow,#timeline,#brush{position:relative;margin-top:12px}#three{width:1280px;height:640px;margin:12px auto;position:relative}${b.css}</style>
<div id="stage"><div class="figs"><div class="fig"></div><div class="fig"></div><div class="fig"></div><div class="fig"></div></div><div id="overview"></div><div id="flow"></div><div id="timeline"></div><div id="brush"></div><div id="three"></div></div>
<script>${b.js.replace(/<\/script/gi, '<\\/script')}</script>`;

async function measure(name, b, kind, args, shot) {
  const html = join(out, name + '.html');
  writeFileSync(html, pageHtml(b));
  const server = await chromium.launchServer({ headless: true, args, ...launch });
  const browser = await chromium.connect(server.wsEndpoint());
  const pid = server.process().pid;
  try {
    const open = async () => {
      const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
      const errors = [], warnings = [];
      page.on('pageerror', e => errors.push(e.message.split('\n')[0]));
      page.on('console', m => { if (m.type() === 'error') errors.push(m.text().slice(0, 200)); else if (m.type() === 'warning') warnings.push(m.text().slice(0, 200)); });
      const cdp = await page.context().newCDPSession(page);
      await cdp.send('Performance.enable'); await cdp.send('HeapProfiler.enable');
      const metrics = async () => Object.fromEntries((await cdp.send('Performance.getMetrics')).metrics.map(x => [x.name, x.value]));
      const heapMb = async () => { await cdp.send('HeapProfiler.collectGarbage'); return +((await metrics()).JSHeapUsedSize / 1048576).toFixed(1); };
      await page.goto(pathToFileURL(html).href);
      return { page, errors, warnings, metrics, heapMb };
    };
    const res = { name };
    if (kind === '2d') {
      const a = await open();
      Object.assign(res, await a.page.evaluate(() => window.BENCH.first()));
      await a.page.close();
      const p = await open();
      const heap0 = await p.heapMb();
      Object.assign(res, await p.page.evaluate(() => window.BENCH.all()));
      res.heapMountMb = await p.heapMb(); res.heapBaselineMb = heap0;
      const m0 = await p.metrics(), c0 = procCpuSeconds(pid);
      const f = await p.page.evaluate(() => window.BENCH.filters());
      const m1 = await p.metrics(), c1 = procCpuSeconds(pid);
      Object.assign(res, f, { filterTaskMs: +((m1.TaskDuration - m0.TaskDuration) * 1000 / 20).toFixed(1), filterBrowserCpuMs: +((c1 - c0) * 1000 / 20).toFixed(1), heapFiltersMb: await p.heapMb() });
      if (shot) await p.page.screenshot({ path: join(shots, name + '.png'), fullPage: true });
      res.errors = [...new Set([...a.errors, ...p.errors])].slice(0, 5); res.warnings = [...new Set([...a.warnings, ...p.warnings])].slice(0, 5);
    } else {
      const p = await open();
      Object.assign(res, await p.page.evaluate(() => window.BENCH.first()));
      res.heapMountMb = await p.heapMb();
      const m0 = await p.metrics(), c0 = procCpuSeconds(pid);
      const r = await p.page.evaluate(() => window.BENCH.rotate(60));
      const m1 = await p.metrics(), c1 = procCpuSeconds(pid);
      Object.assign(res, r, { rotateTaskMs: +((m1.TaskDuration - m0.TaskDuration) * 1000 / 60).toFixed(1), rotateBrowserCpuMs: +((c1 - c0) * 1000 / 60).toFixed(1) });
      if (shot) await p.page.locator('#three').screenshot({ path: join(shots, name + '.png') });
      res.errors = [...new Set(p.errors)].slice(0, 5); res.warnings = [...new Set(p.warnings)].slice(0, 5);
    }
    return res;
  } finally { await browser.close(); await server.close(); }
}

const file = resolve(dir, 'results.json');
const results = ONLY && existsSync(file) ? JSON.parse(readFileSync(file, 'utf8')) : { generatedAt: '', environment: {}, sizes: {}, charts2d: [], views3d: [] };
results.generatedAt = new Date().toISOString();
const wanted = name => !ONLY || name === ONLY || name.split('+').includes(ONLY) || name.startsWith(ONLY + '-');
for (const [name, src] of Object.entries(sizeEntries)) {
  if (!wanted(name)) continue;
  try { results.sizes[name] = (await bundle('size-' + name, src)).size; }
  catch (e) { results.sizes[name] = { skipped: String(e.message).split('\n')[0].slice(0, 160), errors: (e.errors || []).slice(0, 4).map(x => x.text) }; }
}
const b0 = await chromium.launch({ headless: true, ...launch });
const p0 = await b0.newPage();
results.environment = {
  chromium: b0.version(), cpu: cpus()[0].model, cores: cpus().length,
  webgl: await p0.evaluate(() => { const g = document.createElement('canvas').getContext('webgl2'); const e = g && g.getExtension('WEBGL_debug_renderer_info'); return e ? g.getParameter(e.UNMASKED_RENDERER_WEBGL) : 'none'; }),
  viewport: '1440x900, charts 1400 px wide at device pixel ratio 1', headless: true, runs: RUNS,
  set: '40 projects, 4 environments, 6 tenants, 2,000 deploy runs, 5,000 cards (synth() of src/data.js)',
  modes: { 'cpu-raster': '--disable-gpu, the 2D candidates', 'gpu-default': 'default GPU process; on this runner WebGL is software (SwiftShader), the 3D candidates' },
  versions: Object.fromEntries(['echarts', 'echarts-gl', 'uplot', 'chart.js', 'chartjs-chart-matrix', 'chartjs-chart-sankey', 'chartjs-plugin-zoom', 'three'].map(p => [p, version(p)])),
};
await b0.close();

async function candidate(list, name, kind, mode, args) {
  if (!wanted(name)) return;
  const entry = entryFile(name), put = row => { const i = list.findIndex(r => r.name === name && r.mode === mode); if (i >= 0) list[i] = row; else list.push(row); };
  if (!existsSync(resolve(dir, entry))) return put({ name, mode, skipped: `${entry} does not exist yet` });
  let b;
  const install = kind === '2d' ? `import { mount } from '${entry}'; import { install2d } from './harness.js'; install2d(mount);` : `import { mount3d } from '${entry}'; import { install3d } from './harness.js'; install3d(mount3d);`;
  try { b = await bundle(name, install); } catch (e) { return put({ name, mode, skipped: 'bundle failed: ' + String(e.message).split('\n').slice(0, 2).join(' ').slice(0, 240) }); }
  const runs = [];
  for (let i = 0; i < RUNS; i++) {
    try { runs.push(await measure(name, b, kind, args, i === 0)); } catch (e) { runs.push({ name, failed: String(e.message).split('\n')[0].slice(0, 240) }); }
  }
  put({ name, mode, runs });
  console.error(mode, name, JSON.stringify(runs.map(r => r.failed || (kind === '2d' ? [r.firstChartMs, r.allChartsMs, r.filterMs, r.filterP95Ms, r.filterTaskMs, r.heapMountMb, r.domElements] : [r.firstFrameMs, r.rotateFrameMs, r.rotateBrowserCpuMs]))));
}
for (const name of Object.keys(C2D)) await candidate(results.charts2d, name, '2d', 'cpu-raster', ['--disable-gpu']);
for (const name of Object.keys(C3D)) await candidate(results.views3d, name, '3d', 'gpu-default', []);
await candidate(results.views3d, 'canvas2d-own', '3d', 'cpu-raster', ['--disable-gpu']);
writeFileSync(file, compact(results));
console.log(JSON.stringify(results.sizes));

// One run a line: the file is evidence a reviewer reads, and the review cuts long files.
function compact(o, depth = 0) {
  const pad = '  '.repeat(depth), flat = v => v === null || typeof v !== 'object';
  if (flat(o) || depth >= 3 || Object.values(o).every(flat)) return JSON.stringify(o) + (depth ? '' : '\n');
  const rows = Array.isArray(o) ? o.map(v => compact(v, depth + 1)) : Object.entries(o).map(([k, v]) => JSON.stringify(k) + ': ' + compact(v, depth + 1));
  return (Array.isArray(o) ? '[' : '{') + '\n' + rows.map(r => pad + '  ' + r).join(',\n') + '\n' + pad + (Array.isArray(o) ? ']' : '}') + (depth ? '' : '\n');
}
