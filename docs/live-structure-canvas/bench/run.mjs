// Renderer and chart library benchmark for the AGT-3955 Dossier.
// Usage from the repository root:
//   node docs/live-structure-canvas/bench/run.mjs <packages-dir>
// <packages-dir> holds node_modules with the pinned candidates, installed
// outside the repository:
//   npm install --prefix <dir> --ignore-scripts pixi.js@8.23.0 cytoscape@3.35.0 three@0.187.0 uplot@1.6.32 echarts@6.1.0 chart.js@4.6.1
// Writes bench/results.json. Needs the frontend dev dependencies (esbuild, playwright-core).
import { createRequire } from 'node:module';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { dirname, resolve, join } from 'node:path';
import { mkdtempSync, writeFileSync, readFileSync } from 'node:fs';
import { tmpdir, cpus } from 'node:os';
import { gzipSync, brotliCompressSync } from 'node:zlib';

const dir = dirname(fileURLToPath(import.meta.url));
const require = createRequire(resolve(dir, '../../../frontend/package.json'));
const esbuild = require('esbuild');
const { chromium } = require('playwright-core');
const pkgs = resolve(process.argv[2] || process.env.LSC_BENCH_PACKAGES || '');
if (!process.argv[2] && !process.env.LSC_BENCH_PACKAGES) throw new Error('Pass the packages directory');
const nodePaths = [join(pkgs, 'node_modules')];
const out = mkdtempSync(join(tmpdir(), 'lsc-bench-'));
const version = p => JSON.parse(readFileSync(join(pkgs, 'node_modules', p, 'package.json'), 'utf8')).version;

async function bundle(entry, name, contents) {
  const r = await esbuild.build({
    ...(contents ? { stdin: { contents, resolveDir: dir, loader: 'js' } } : { entryPoints: [resolve(dir, entry)] }),
    bundle: true, minify: true, format: 'iife', write: false, nodePaths, outdir: join(out, name),
    loader: { '.css': 'css' }, target: 'es2022', logLevel: 'error', legalComments: 'none',
  });
  const js = r.outputFiles.filter(f => f.path.endsWith('.js')).map(f => f.contents);
  const css = r.outputFiles.filter(f => f.path.endsWith('.css')).map(f => f.contents);
  const buf = Buffer.concat([...js, ...css].map(b => Buffer.from(b)));
  return { js: Buffer.concat(js.map(b => Buffer.from(b))).toString(), css: Buffer.concat(css.map(b => Buffer.from(b))).toString(),
    size: { rawKb: +(buf.length / 1024).toFixed(1), gzipKb: +(gzipSync(buf, { level: 9 }).length / 1024).toFixed(1), brotliKb: +(brotliCompressSync(buf).length / 1024).toFixed(1) } };
}

// Lazy chunk cost: only the modules a product chunk would import.
const sizeEntries = {
  'own-canvas2d': `import e from './../src/engine.js'; import l from './../src/layers.js'; import s from './../src/spec.js'; globalThis.x = [e, l, s];`,
  'own-engine-only': `import e from './../src/engine.js'; globalThis.x = e;`,
  'pixi-webgl': `import { Application, Container, Graphics, BitmapText } from 'pixi.js'; globalThis.x = [Application, Container, Graphics, BitmapText];`,
  cytoscape: `import c from 'cytoscape'; globalThis.x = c;`,
  'three-3d-reference': `import { WebGLRenderer, Scene, OrthographicCamera, Mesh, BoxGeometry, MeshBasicMaterial, LineSegments, LineBasicMaterial, BufferGeometry } from 'three'; globalThis.x = [WebGLRenderer, Scene, OrthographicCamera, Mesh, BoxGeometry, MeshBasicMaterial, LineSegments, LineBasicMaterial, BufferGeometry];`,
  uplot: `import u from 'uplot'; import 'uplot/dist/uPlot.min.css'; globalThis.x = u;`,
  echarts: `import * as e from 'echarts/core'; import { LineChart, BarChart } from 'echarts/charts'; import { GridComponent, TooltipComponent, LegendComponent, AriaComponent } from 'echarts/components'; import { CanvasRenderer } from 'echarts/renderers'; e.use([LineChart, BarChart, GridComponent, TooltipComponent, LegendComponent, AriaComponent, CanvasRenderer]); globalThis.x = e;`,
  chartjs: `import { Chart, LineController, BarController, LineElement, BarElement, PointElement, LinearScale, CategoryScale, Tooltip, Legend, Filler } from 'chart.js'; Chart.register(LineController, BarController, LineElement, BarElement, PointElement, LinearScale, CategoryScale, Tooltip, Legend, Filler); globalThis.x = Chart;`,
};

function procCpuSeconds(rootPid) {
  // CPU time of the whole browser process tree (renderer, GPU and browser processes).
  const fs = require('node:fs');
  const kids = new Map();
  for (const p of fs.readdirSync('/proc').filter(x => /^\d+$/.test(x))) {
    try { const st = fs.readFileSync(`/proc/${p}/stat`, 'utf8'); const f = st.slice(st.lastIndexOf(')') + 2).split(' '); kids.set(+p, { ppid: +f[1], cpu: (+f[11] + +f[12]) }); } catch { /* gone */ }
  }
  let total = 0; const stack = [rootPid];
  while (stack.length) { const p = stack.pop(); const k = kids.get(p); if (k) total += k.cpu; for (const [c, v] of kids) if (v.ppid === p) stack.push(c); }
  return total / 100; // clock ticks to seconds (USER_HZ 100)
}

async function measure(name, b, kind, args = []) {
  const html = join(out, name + '.html');
  writeFileSync(html, `<!doctype html><meta charset="utf-8"><style>body{margin:0;background:#fcfcfb;font-family:system-ui}${b.css}</style><div id="host"></div><script>${b.js.replace(/<\/script/gi, '<\\/script')}</script>`);
  const server = await chromium.launchServer({ headless: true, args, ...(process.env.ATP_PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH ? { executablePath: process.env.ATP_PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH } : {}) });
  const browser = await chromium.connect(server.wsEndpoint());
  browserPid = server.process().pid;
  try {
    const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
    const errors = [];
    page.on('pageerror', e => errors.push(e.message));
    const cdp = await page.context().newCDPSession(page);
    await cdp.send('Performance.enable');
    const metrics = async () => Object.fromEntries((await cdp.send('Performance.getMetrics')).metrics.map(x => [x.name, x.value]));
    await page.goto(pathToFileURL(html).href);
    const res = { name };
    res.init = await page.evaluate(() => window.BENCH.init());
    const m0 = await metrics();
    res.heapMb = +(m0.JSHeapUsedSize / 1048576).toFixed(1);
    if (kind === 'canvas') {
      await page.evaluate(() => window.BENCH.panZoom(1000));
      res.panZoom = await page.evaluate(() => window.BENCH.panZoom(5000));
      const idle = async (ms, setup) => {
        if (setup) await page.evaluate(setup);
        await page.waitForTimeout(600);
        const a = await metrics(), f0 = await page.evaluate(() => window.BENCH.frameCounter());
        const pa = rootCpu(), t0 = Date.now();
        await page.waitForTimeout(ms);
        const b2 = await metrics(), f1 = await page.evaluate(() => window.BENCH.frameCounter());
        const pb = rootCpu(), dt = (Date.now() - t0) / 1000;
        return { mainThreadCpuPct: +((b2.TaskDuration - a.TaskDuration) / dt * 100).toFixed(1), browserCpuPct: pa == null ? null : +((pb - pa) / dt * 100).toFixed(1), framesPerSec: f0 == null ? null : +((f1 - f0) / dt).toFixed(1) };
      };
      res.idle = await idle(5000, () => window.BENCH.flow(false));
      res.flow = await idle(5000, () => window.BENCH.flow(true));
      await page.evaluate(() => window.BENCH.flow(false));
    }
    res.errors = errors;
    return res;
  } finally { await browser.close(); await server.close(); }
}
let browserPid = null;
function rootCpu() { return browserPid ? procCpuSeconds(browserPid) : null; }

const RUNS = +(process.env.LSC_BENCH_RUNS || 3);
const results = { generatedAt: new Date().toISOString(), environment: {}, sizes: {}, renderers: [], charts: [] };
for (const [name, src] of Object.entries(sizeEntries)) results.sizes[name] = (await bundle(null, 'size-' + name, src)).size;
const b0 = await chromium.launch({ headless: true });
const p0 = await b0.newPage();
results.environment = {
  chromium: b0.version(), cpu: cpus()[0].model, cores: cpus().length,
  webgl: await p0.evaluate(() => { const g = document.createElement('canvas').getContext('webgl2'); const e = g && g.getExtension('WEBGL_debug_renderer_info'); return e ? g.getParameter(e.UNMASKED_RENDERER_WEBGL) : 'none'; }),
  viewport: '1440x900, drawing surface 1280x800 at device pixel ratio 1', headless: true,
  versions: { 'pixi.js': version('pixi.js'), cytoscape: version('cytoscape'), three: version('three'), uplot: version('uplot'), echarts: version('echarts'), 'chart.js': version('chart.js') },
};
await b0.close();
// Two raster paths: the default GPU process (software WebGL on a machine
// without GPU access) and CPU raster with the GPU process disabled.
const MODES = { 'gpu-default': [], 'cpu-raster': ['--disable-gpu'] };
for (const [mode, args] of Object.entries(MODES)) {
  for (const [name, entry] of [['own-canvas2d', 'own.js'], ['own-canvas2d-semantic', 'own-semantic.js'], ['pixi-webgl', 'pixi.js'], ['cytoscape', 'cyto.js']]) {
    const runs = [];
    for (let i = 0; i < RUNS; i++) {
      try { runs.push(await measure(name, await bundle(entry, name), 'canvas', args)); }
      catch (e) { runs.push({ name, failed: String(e.message).split('\n')[0] }); }
    }
    results.renderers.push({ name, mode, runs });
    console.error(mode, name, JSON.stringify(runs.map(r => r.failed || [r.panZoom?.fps, r.panZoom?.renderFps, r.panZoom?.frameP95, r.idle?.browserCpuPct, r.flow?.browserCpuPct, r.flow?.framesPerSec])));
  }
}
if (process.env.LSC_BENCH_SKIP_CHARTS) { writeFileSync(resolve(dir, 'results.json'), compact(results)); process.exit(0); }
for (const [name, entry] of [['uplot', 'chart-uplot.js'], ['echarts', 'chart-echarts.js'], ['chartjs', 'chart-chartjs.js']]) {
  const runs = [];
  for (let i = 0; i < RUNS; i++) runs.push(await measure(name, await bundle(entry, name), 'chart'));
  results.charts.push({ name, runs });
  console.error(name, JSON.stringify(runs.map(r => r.init)));
}
writeFileSync(resolve(dir, 'results.json'), compact(results));
console.log(JSON.stringify(results.sizes, null, 1));
// One run a line: the file is evidence a reviewer reads, and the review cuts long files.
function compact(o, depth = 0) {
  const pad = '  '.repeat(depth), flat = v => v === null || typeof v !== 'object';
  if (flat(o) || depth >= 3 || Object.values(o).every(flat)) return JSON.stringify(o) + (depth ? '' : '\n');
  const rows = Array.isArray(o) ? o.map(v => compact(v, depth + 1)) : Object.entries(o).map(([k, v]) => JSON.stringify(k) + ': ' + compact(v, depth + 1));
  return (Array.isArray(o) ? '[' : '{') + '\n' + rows.map(r => pad + '  ' + r).join(',\n') + '\n' + pad + (Array.isArray(o) ? ']' : '}') + (depth ? '' : '\n');
}
