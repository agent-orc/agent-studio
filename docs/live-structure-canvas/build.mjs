// Builds the self-contained index.html of the AGT-3955 Dossier and writes the
// view specification schema. Run from the repository root:
//   node docs/live-structure-canvas/build.mjs
// Inputs: src/index.template.html, src/prototype.html, src/canvas.css,
// src/{engine,layers,spec,model,app}.js, bench/results.json, the files in
// assets/ and the canonical article template style.
// The build fails when the concept review snapshot (review-snapshot.mjs)
// would not hold the whole Dossier.
import { createRequire } from 'node:module';
import { readFileSync, writeFileSync, mkdirSync, existsSync, readdirSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { gzipSync } from 'node:zlib';
import { reviewSnapshot, LIMITS } from './review-snapshot.mjs';

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
// The inlined prototype is minified (esbuild from the frontend toolchain, npm --prefix frontend ci):
// the review snapshot leaves out a file above 256,000 characters, and src/ stays the readable source.
const { transformSync } = require(resolve(dir, '../../frontend/node_modules/esbuild'));
const minify = (code, loader) => transformSync(code, { loader, minify: true, legalComments: 'none' }).code.trim();
const scripts = ['engine.js', 'layers.js', 'spec.js', 'model.js', 'app.js'].map(f => minify(read(src(f)), 'js'));
const MAX_INDEX_CHARS = 240000;
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
  <span class="figure-as-of"><span>Captured ${timeOf('prototype')} from this Dossier's prototype by <code>capture.mjs</code>, 1440 px, only this layer on.</span></span></figcaption>
</figure>`).join('\n');
}
function mobileGallery() {
  return Layers.LAYERS.flatMap(l => ['light', 'dark'].map(t => `<figure><img src="assets/layer-${l.id}-${t}-390.png" alt="${attr(l.title + ' layer at 390 px, ' + t + ' theme.')}" loading="lazy"><figcaption>${esc(l.title)}, ${t}</figcaption></figure>`)).join('');
}
// Capture dates come from the manifest's group of each kind, not from the text.
const manifest = JSON.parse(read(resolve(dir, 'assets/capture-manifest.json'))).groups;
const longDate = iso => new Date(iso).toLocaleDateString('en-GB', { day: 'numeric', month: 'long', year: 'numeric', timeZone: 'UTC' });
const timeOf = kind => `<time datetime="${manifest[kind].capturedAt.slice(0, 10)}">${longDate(manifest[kind].capturedAt)}</time>`;
const groupOf = file => Object.keys(manifest).find(k => manifest[k].frames.includes(file));
// Inventory of the captures, read from the files, so a text-only review can check them.
function capturesTable() {
  const layerTitle = Object.fromEntries(Layers.LAYERS.map(l => [l.id, l.title]));
  const shows = name => {
    let m;
    if ((m = name.match(/^layer-([a-z]+)-/))) return `Layer ${layerTitle[m[1]]}, only this layer on`;
    if ((m = name.match(/^level-(\d)-/))) return `Semantic zoom level ${m[1]}`;
    if (name.startsWith('view-2d')) return '2D view, Performance and Health layers';
    if (name.startsWith('view-25d')) return '2.5D view of the same scene';
    if (name.startsWith('assessment-rejected')) return 'Rejected view specification, facts-only fallback';
    if (name.startsWith('replay-tunnel-down')) return 'Replay at second 25, tunnel down';
    if (name.startsWith('today-structure-view')) return "Today's Structure view of the product, API answered by a role-named fixture";
    if (name === 'replay-recording-poster.png') return 'Frame of the replay recording';
    if (name.startsWith('replay-filmstrip')) return 'Six moments of the live replay with captions, in one image';
    if (name === 'replay-recording.webm') return 'Screen recording of the replay at speed 2, about 25 seconds';
    throw new Error('Unclassified capture ' + name);
  };
  const files = readdirSync(resolve(dir, 'assets')).filter(f => /\.(png|webm)$/.test(f)).sort();
  for (const l of Layers.LAYERS) for (const t of ['light', 'dark']) for (const w of [1440, 390]) {
    if (!files.includes(`layer-${l.id}-${t}-${w}.png`)) throw new Error(`Missing capture layer-${l.id}-${t}-${w}.png`);
  }
  const rows = files.map(f => {
    const buf = readFileSync(resolve(dir, 'assets', f));
    const png = f.endsWith('.png');
    const px = png ? `${buf.readUInt32BE(16)} × ${buf.readUInt32BE(20)}` : '1280 × 800, WebM';
    const theme = /-dark[-.]/.test(f) ? 'dark' : 'light';
    const viewport = f.startsWith('today-') ? '1600' : /-390\.png$/.test(f) ? '390' : '1440';
    const group = groupOf(f);
    if (!group) throw new Error(`${f} is in assets/ but in no group of capture-manifest.json`);
    return `<tr><td><code>assets/${f}</code></td><td>${esc(shows(f))}</td><td>${theme}</td><td>${viewport} px</td><td>${px}</td><td>${buf.length.toLocaleString('en-US')}</td><td>${manifest[group].capturedAt.slice(0, 10)}</td></tr>`;
  });
  const n = Layers.LAYERS.length;
  return `<p>${n} layers × light and dark × 1440 px and 390 px: ${n * 4} of ${n * 4} captures present.</p>
<details><summary>${files.length} files with what they show, theme, viewport, size and capture date</summary><div class="tablewrap media-breakout"><table class="captures"><thead><tr><th>File</th><th>Shows</th><th>Theme</th><th>Viewport</th><th>Pixels</th><th>Bytes</th><th>Captured</th></tr></thead><tbody>
${rows.join('\n')}
</tbody></table></div></details>`;
}
const realPath = resolve(dir, 'assets/real-scene-metrics.json');
const real = existsSync(realPath) ? JSON.parse(read(realPath)) : null;
const flowFps = name => { const r = bench && bench.renderers.find(x => x.name === name && x.mode === 'cpu-raster'); return r ? fmt(med(r.runs.filter(x => !x.failed), x => x.flow.framesPerSec), 0) : 'n/a'; };

let html = read(src('index.template.html'));
const replace = (k, v) => { if (!html.includes(k)) throw new Error('Template lacks ' + k); html = html.split(k).join(v); };
replace('{{ARTICLE_STYLE}}', articleStyle);
replace('{{CANVAS_CSS}}', minify(read(src('canvas.css')), 'css'));
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
replace('{{CAPTURES}}', capturesTable());
replace('{{PRODUCT_TIME}}', timeOf('product'));
replace('{{PRODUCT_COMMIT}}', manifest.product.commit.slice(0, 9));
replace('{{PROTO_TIME}}', timeOf('prototype'));
replace('{{VIDEO_TIME}}', timeOf('video'));
replace('{{FILMSTRIP_TIME}}', timeOf('filmstrip'));
replace('{{REAL_DATE}}', real ? longDate(real.measuredAt) : 'pending');
replace('{{INDEX_LIMIT}}', MAX_INDEX_CHARS.toLocaleString('en-US'));
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
if (html.includes('—')) throw new Error('Em dash found');
// The page states what the review snapshot holds; the statement is part of the page, so
// write, measure and repeat until the numbers stand.
const entry = 'docs/live-structure-canvas/index.html';
const own = 'docs/live-structure-canvas/';
const n = v => v.toLocaleString('en-US');
const withFacts = facts => html.split('{{SNAPSHOT_FACTS}}').join(facts);
if (!html.includes('{{SNAPSHOT_FACTS}}')) throw new Error('Template lacks {{SNAPSHOT_FACTS}}');
let snapshot = null, facts = 'measured by the build';
for (let i = 0; i < 6; i++) {
  writeFileSync(resolve(dir, 'index.html'), withFacts(facts));
  snapshot = reviewSnapshot(resolve(dir, '../..'), entry);
  const text = snapshot.files.filter(f => f.kind === 'text'), binary = snapshot.files.filter(f => f.kind === 'binary');
  const next = `today ${n(text[0].characters)} characters for this page and ${n(snapshot.textCharacters)} for the ${text.length} text files together, ${snapshot.inspected} files with the ${binary.length - 1} images and the recording, which it can only name`;
  if (next === facts) break;
  facts = next;
}
html = withFacts(facts);
if (/\{\{[A-Z_:a-z-]+\}\}/.test(html)) throw new Error('Unresolved placeholder: ' + html.match(/\{\{[A-Z_:a-z-]+\}\}/)[0]);
if (html.length > MAX_INDEX_CHARS) throw new Error(`index.html has ${html.length} characters; the review snapshot needs it below ${MAX_INDEX_CHARS}`);
writeFileSync(resolve(dir, 'index.html'), html);
snapshot = reviewSnapshot(resolve(dir, '../..'), entry);
const foreign = snapshot.files.filter(f => !f.path.startsWith(own)).map(f => f.path);
if (snapshot.problems.length || foreign.length) {
  throw new Error('The concept review snapshot would not hold the whole Dossier:\n' + [...snapshot.problems, ...foreign.map(f => f + ': a linked file outside this Dossier takes review budget.')].join('\n'));
}
const prodGz = gzipSync(Buffer.from(['engine.js', 'layers.js', 'spec.js'].map(f => read(src(f))).join('\n'))).length / 1024;
console.log(JSON.stringify({ bytes: Buffer.byteLength(html), characters: html.length, prototypeSourceKb: +prodKb.toFixed(1), prototypeSourceGzipKb: +prodGz.toFixed(1),
  reviewSnapshot: { files: snapshot.inspected, fileLimit: LIMITS.files, textCharacters: snapshot.textCharacters, characterLimit: LIMITS.totalCharacters, problems: snapshot.problems.length } }, null, 2));
