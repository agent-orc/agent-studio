// Builds the self-contained index.html of the AGT-3955 Dossier and writes the
// view specification schema. Run from the repository root:
//   node docs/live-structure-canvas/build.mjs
// Inputs: src/index.template.html, src/prototype.html, src/canvas.css,
// src/{engine,layers,spec,model,app}.js, bench/results.json and the
// canonical article template style.
import { createRequire } from 'node:module';
import { readFileSync, writeFileSync, mkdirSync, existsSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { gzipSync } from 'node:zlib';

const dir = dirname(fileURLToPath(import.meta.url));
const src = f => resolve(dir, 'src', f);
const read = f => readFileSync(f, 'utf8');
const require = createRequire(import.meta.url);
const Spec = require(src('spec.js'));
const Model = require(src('model.js'));
const Layers = require(src('layers.js'));

const template = read(resolve(dir, '../app/templates/article-document-v2.html'));
const styleStart = template.indexOf('<style data-article-template="v2">');
const articleStyle = template.slice(styleStart, template.indexOf('</style>', styleStart) + '</style>'.length);

// JSON Schema file: the custom plainText keyword becomes a pattern.
const toJsonSchema = s => JSON.parse(JSON.stringify(s), (k, v) => {
  if (v && typeof v === 'object' && v.plainText) { const { plainText, ...rest } = v; void plainText; return { ...rest, pattern: rest.pattern || '^[^<>]*$', 'x-plain-text': true }; }
  return v;
});
mkdirSync(resolve(dir, 'schema'), { recursive: true });
writeFileSync(resolve(dir, 'schema/structure-view-spec.v1.schema.json'), JSON.stringify(toJsonSchema(Spec.schema), null, 2) + '\n');
writeFileSync(resolve(dir, 'schema/example-operations-warden.json'), JSON.stringify(Model.specs['operations-warden'], null, 2) + '\n');

const esc = s => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
const scripts = ['engine.js', 'layers.js', 'spec.js', 'model.js', 'app.js'].map(f => read(src(f)));
const prodKb = ['engine.js', 'layers.js', 'spec.js'].reduce((s, f) => s + Buffer.byteLength(read(src(f))), 0) / 1024;

// Benchmark tables from bench/results.json (median of the runs).
const benchPath = resolve(dir, 'bench/results.json');
const bench = existsSync(benchPath) ? JSON.parse(read(benchPath)) : null;
const med = (runs, f) => { const v = runs.map(f).filter(x => x != null && !Number.isNaN(x)).sort((a, b) => a - b); return v.length ? v[Math.floor(v.length / 2)] : null; };
const fmt = (v, d = 1) => (v == null ? 'n/a' : Number(v).toFixed(d));
function rendererTable() {
  if (!bench) return '<p>Benchmark not run.</p>';
  const names = { 'own-canvas2d': 'A · Own Canvas 2D engine', 'own-canvas2d-semantic': 'A · same, semantic zoom on', 'pixi-webgl': 'B · PixiJS 8 (WebGL 2D)', cytoscape: 'C · Cytoscape.js 3 (compound graph)' };
  const size = { 'own-canvas2d': 'own-canvas2d', 'own-canvas2d-semantic': 'own-canvas2d', 'pixi-webgl': 'pixi-webgl', cytoscape: 'cytoscape' };
  const rows = bench.renderers.filter(r => r.mode === 'cpu-raster').map(r => {
    const ok = r.runs.filter(x => !x.failed);
    const gpu = bench.renderers.find(x => x.name === r.name && x.mode === 'gpu-default');
    const gok = gpu ? gpu.runs.filter(x => !x.failed) : [];
    const sz = bench.sizes[size[r.name]];
    return `<tr><td>${names[r.name]}</td><td>${fmt(sz.gzipKb)} kB <span class="dim">(${fmt(sz.rawKb)} raw)</span></td>`
      + `<td>${fmt(med(ok, x => x.panZoom.renderFps), 0)} <span class="dim">p95 ${fmt(med(ok, x => x.panZoom.frameP95), 0)} ms</span></td>`
      + `<td>${fmt(med(gok, x => x.panZoom.renderFps), 0)}</td>`
      + `<td>${fmt(med(ok, x => x.idle.browserCpuPct))} %</td>`
      + `<td>${fmt(med(ok, x => x.flow.browserCpuPct), 0)} % <span class="dim">at ${fmt(med(ok, x => x.flow.framesPerSec), 0)} fps</span></td>`
      + `<td>${fmt(med(ok, x => x.heapMb))} MB</td><td>${fmt(med(ok, x => x.init.firstRenderMs), 0)} ms</td></tr>`;
  });
  return '<div class="tablewrap"><table class="bench"><thead><tr><th>Candidate</th><th>Lazy chunk, gzip</th><th>Pan and zoom, frames a second (CPU raster)</th><th>Same, default GPU path</th><th>Idle CPU</th><th>Flow animation CPU</th><th>JS heap</th><th>First render</th></tr></thead><tbody>'
    + rows.join('') + '</tbody></table></div>';
}
function chartTable() {
  if (!bench) return '';
  const names = { uplot: 'uPlot 1', echarts: 'Apache ECharts 6 (line, bar, grid, tooltip, legend, aria, canvas)', chartjs: 'Chart.js 4 (line, bar, scales, tooltip, legend, filler)' };
  const lic = { uplot: 'MIT', echarts: 'Apache-2.0', chartjs: 'MIT' };
  return '<div class="tablewrap"><table class="bench"><thead><tr><th>Library</th><th>Lazy chunk, gzip</th><th>Three charts, first render</th><th>Live update of 6 × 1,440 points</th><th>JS heap</th><th>Licence</th></tr></thead><tbody>'
    + bench.charts.map(c => `<tr><td>${names[c.name]}</td><td>${fmt(bench.sizes[c.name].gzipKb)} kB <span class="dim">(${fmt(bench.sizes[c.name].rawKb)} raw)</span></td><td>${fmt(med(c.runs, x => x.init.firstRenderMs), 0)} ms</td><td>${fmt(med(c.runs, x => x.init.updateMs))} ms</td><td>${fmt(med(c.runs, x => x.heapMb))} MB</td><td>${lic[c.name]}</td></tr>`).join('')
    + '</tbody></table></div>';
}
function benchEnv() {
  if (!bench) return '';
  const e = bench.environment;
  return `Measured ${bench.generatedAt.slice(0, 10)} on the task runner: ${esc(e.cpu)}, ${e.cores} threads, headless Chromium ${e.chromium}, ${e.viewport}. WebGL there is ${esc(e.webgl)}: the runner has no usable GPU. Median of ${bench.renderers[0].runs.length} runs. Versions: ${Object.entries(e.versions).map(([k, v]) => k + ' ' + v).join(', ')}.`;
}
function inventoryTable() {
  const cls = { must: 'Must back up', escrow: 'Must escrow', reproducible: 'Reproducible', cache: 'Cache' };
  return '<div class="tablewrap"><table><thead><tr><th>Data location</th><th>Where</th><th>Size</th><th>Class</th><th>Copies today</th><th>Recovery point</th><th>Evidence</th></tr></thead><tbody>'
    + Model.dataLocations.map(l => {
      const s = Layers.locStatus(l);
      const copies = l.copies.length ? l.copies.map(c => ({ 'git-remote': 'git remote, ' + (c.at ? 'last push ' + c.at.slice(11, 16) : ''), 'local-snapshot': 'manual snapshot ' + (c.at || '').slice(0, 10) + ', same disk', origin: 'project remotes', registry: 'registry' }[c.kind] || c.kind)).join('; ') : 'none';
      const rpo = s === 'ok' ? 'minutes' : s === 'local' ? '8 days, same disk' : s === 'gap' ? 'none' : 'rebuild';
      const node = Model.nodes.find(n => n.id === l.node);
      return `<tr data-status="${s}"><td><b>${esc(l.label)}</b><br><span class="dim">${esc(l.detail)}</span></td><td>${esc(node.label)}</td><td>${Layers.gb(l.sizeGb)}</td><td>${cls[l.cls]}</td><td class="st-${s}">${esc(copies)}</td><td>${rpo}</td><td><span class="dim">${esc(l.source || 'illustrative')}</span></td></tr>`;
    }).join('') + '</tbody></table></div>';
}

const wb = JSON.parse(read(resolve(dir, 'workbench.json')));
const attr = s => esc(s).replace(/"/g, '&quot;');
function decisionsHtml() {
  return wb.openDecisions.map(d => `<div class="decision" id="${d.id}" data-decision-id="${d.decisionId}" data-decision-kind="single" data-decision-label="${attr(d.id + ' ' + d.title)}">
  <h3>${d.id} · ${esc(d.title)}</h3><p>${esc(d.question)}</p>
${Object.entries(d.options).map(([k, v]) => `  <label data-option-id="${d.decisionId}-${k.toLowerCase()}" data-option-label="${attr(k + ' · ' + v + (k === d.recommended ? ' (Recommendation)' : ''))}"><input type="radio" name="${d.decisionId}" value="${k}"><span><b>${k} · ${esc(v)}</b>${k === d.recommended ? '<span class="rec">Recommended</span>' : ''}</span></label>`).join('\n')}
  <p class="dim">${esc(d.rationale)}</p>
  <div data-comment="Note (optional)"><textarea placeholder="Note (optional)" aria-label="Note on ${attr(d.id)} (optional)"></textarea></div>
</div>`).join('\n');
}
function slicesHtml() {
  return '<div class="tablewrap"><table><thead><tr><th>Slice</th><th>Delivers</th><th>After</th></tr></thead><tbody>'
    + wb.implementationTasks.map(t => `<tr><td><b>${t.id}</b> ${esc(t.title)}</td><td>${esc(t.acceptanceScope)}</td><td>${t.dependsOn.length ? t.dependsOn.join(', ') : 'none'}</td></tr>`).join('')
    + '</tbody></table></div>';
}
function layerRows() {
  return Layers.LAYERS.map(l => `<tr><td><b>${esc(l.title)}</b></td><td>${esc(l.data)}</td><td>${esc(l.encoding)}</td></tr>`).join('');
}
function layerFigures() {
  return Layers.LAYERS.map(l => `<figure class="evidence-figure media-breakout" id="fig-layer-${l.id}">
  <div class="evidence-captures">
    <div class="evidence-capture"><span class="capture-theme">Light</span><img src="assets/layer-${l.id}-light-1440.png" alt="${attr(l.title + ' layer at 1440 px, light theme: ' + l.encoding)}" loading="lazy"></div>
    <div class="evidence-capture"><span class="capture-theme">Dark</span><img src="assets/layer-${l.id}-dark-1440.png" alt="${attr(l.title + ' layer at 1440 px, dark theme.')}" loading="lazy"></div>
  </div>
  <figcaption><span class="figure-claim"><b>${esc(l.title)}.</b> ${esc(l.encoding)}</span>
  <span class="figure-as-of"><span>Captured <time datetime="2026-11-17">17 November 2026</time> from this Dossier's prototype by <code>capture.mjs</code>, 1440 px, only this layer on.</span></span></figcaption>
</figure>`).join('\n');
}
function mobileGallery() {
  return Layers.LAYERS.flatMap(l => ['light', 'dark'].map(t => `<figure><img src="assets/layer-${l.id}-${t}-390.png" alt="${attr(l.title + ' layer at 390 px, ' + t + ' theme.')}" loading="lazy"><figcaption>${esc(l.title)}, ${t}</figcaption></figure>`)).join('');
}
const realPath = resolve(dir, 'assets/real-scene-metrics.json');
const real = existsSync(realPath) ? JSON.parse(read(realPath)) : null;
const flowFps = name => { const r = bench && bench.renderers.find(x => x.name === name && x.mode === 'cpu-raster'); return r ? fmt(med(r.runs.filter(x => !x.failed), x => x.flow.framesPerSec), 0) : 'n/a'; };

let html = read(src('index.template.html'));
const replace = (k, v) => { if (!html.includes(k)) throw new Error('Template lacks ' + k); html = html.split(k).join(v); };
replace('{{ARTICLE_STYLE}}', articleStyle);
replace('{{CANVAS_CSS}}', read(src('canvas.css')));
replace('{{PROTOTYPE}}', read(src('prototype.html')));
replace('{{BENCH_TABLE}}', rendererTable());
replace('{{CHART_TABLE}}', chartTable());
replace('{{BENCH_ENV}}', benchEnv());
replace('{{INVENTORY}}', inventoryTable());
replace('{{SPEC_EXAMPLE}}', esc(JSON.stringify(Model.specs['operations-warden'], null, 2)));
replace('{{DECISIONS}}', decisionsHtml());
replace('{{SLICES}}', slicesHtml());
replace('{{LAYER_ROWS}}', layerRows());
replace('{{LAYER_FIGURES}}', layerFigures());
replace('{{MOBILE_GALLERY}}', mobileGallery());
const gz = k => (bench && bench.sizes[k] ? fmt(bench.sizes[k].gzipKb) : 'n/a');
replace('{{OWN_GZ}}', gz('own-canvas2d'));
replace('{{UPLOT_GZ}}', gz('uplot'));
replace('{{CYTO_GZ}}', gz('cytoscape'));
replace('{{PIXI_GZ}}', gz('pixi-webgl'));
replace('{{THREE_GZ}}', gz('three-3d-reference'));
replace('{{A_FLOW_FPS}}', flowFps('own-canvas2d'));
replace('{{C_FLOW_FPS}}', flowFps('cytoscape'));
replace('{{REAL_FPS}}', real ? `a frame costs ${real.panFrameMsP50} ms (p95 ${real.panFrameMsP95} ms) during a scripted pan and zoom at 1440 px, ${real.panFrameMsP95 < 16.7 ? "inside" : "above"} the 16.7 ms of 60 frames a second` : "pending");
replace('{{REAL_IDLE}}', real ? `${real.idleFrames} frames in ${real.idleSeconds} seconds idle; ${real.hiddenFrames} frames in ${real.idleSeconds} seconds with the tab hidden` : 'pending');
replace('{{REAL_FLOW}}', real ? `${real.flowFps} frames a second with the Performance layer on, ${real.flowCpuPct} % of one core for the page` : 'pending');
replace('{{REAL_DELTAS}}', real ? `${real.deltas} deltas over the ${real.replaySeconds}-second replay produced ${real.updates} visual updates` : 'pending');
replace('{{SCRIPT}}', scripts.join('\n').replace(/<\/script/gi, '<\\/script'));
if (/\{\{[A-Z_:a-z-]+\}\}/.test(html)) throw new Error('Unresolved placeholder: ' + html.match(/\{\{[A-Z_:a-z-]+\}\}/)[0]);
if (html.includes('—')) throw new Error('Em dash found');
writeFileSync(resolve(dir, 'index.html'), html);
const prodGz = gzipSync(Buffer.from(['engine.js', 'layers.js', 'spec.js'].map(f => read(src(f))).join('\n'))).length / 1024;
console.log(JSON.stringify({ bytes: Buffer.byteLength(html), prototypeSourceKb: +prodKb.toFixed(1), prototypeSourceGzipKb: +prodGz.toFixed(1) }, null, 2));
