// Builds the pages of the AGT-3955 Dossier. Run from the repository root:
//   node docs/live-structure-canvas/build.mjs
// The five pages are their own source. The build only refreshes the regions
// between <!--g:KEY--> and <!--/g--> in place (style, chapter navigation,
// tables and numbers from workbench.json, bench/results.json, src/model.js,
// src/layers.js and assets/), writes prototype.html (src/prototype.body.html
// with src/canvas.css and the six src/*.js files minified and inlined) and
// the schema files. It fails when the concept review would not receive a page
// whole (review-input.mjs).
import { createRequire } from 'node:module';
import { readFileSync, writeFileSync, mkdirSync, existsSync, readdirSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { gzipSync } from 'node:zlib';
import { reviewSnapshot, reviewDiff, LIMITS } from './review-input.mjs';

const dir = dirname(fileURLToPath(import.meta.url));
const src = f => resolve(dir, 'src', f);
const read = f => readFileSync(f, 'utf8');
const require = createRequire(import.meta.url);
const Spec = require(src('spec.js'));
const Model = require(src('model.js'));
const Layers = require(src('layers.js'));
// esbuild comes from the frontend toolchain (npm --prefix frontend ci).
const { transformSync } = require(resolve(dir, '../../frontend/node_modules/esbuild'));
const minify = (code, loader) => transformSync(code, { loader, minify: true, legalComments: 'none' }).code.trim();
const esc = s => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
const attr = s => esc(s).replace(/"/g, '&quot;');

const PAGES = [
  ['index.html', 'Decision page'], ['context.html', 'Context and canvas'], ['layers.html', 'Layers, backup and warnings'],
  ['live.html', 'Live updates and assessments'], ['stack.html', 'Renderer, charts, data and budget'], ['prototype.html', 'Live prototype'],
];

// House style: the canonical article block, minified so that every page stays
// inside the review's 40,000 characters a file; then the Dossier's own parts.
const template = read(resolve(dir, '../app/templates/article-document-v2.html'));
const styleAt = template.indexOf('<style data-article-template="v2">');
const articleCss = minify(template.slice(template.indexOf('>', styleAt) + 1, template.indexOf('</style>', styleAt)), 'css');
const parts = read(src('dossier.css')).split(/\/\* @part (.+?) \*\/\n/).slice(1);
function styleFor(html, extra = '') {
  let own = '';
  for (let i = 0; i < parts.length; i += 2) if (parts[i] === 'always' || html.includes(parts[i])) own += parts[i + 1];
  return `<style data-article-template="v2">/* docs/app/templates/article-document-v2.html, minified by build.mjs */${articleCss}</style>\n`
    + `<style data-dossier="live-structure-canvas">${minify(own + extra, 'css')}</style>`;
}
const navFor = file => '<nav class="chapters" aria-label="Pages of this Dossier"><ol>'
  + PAGES.map(([f, t]) => `<li><a href="${f}"${f === file ? ' aria-current="page"' : ''}>${t}</a></li>`).join('') + '</ol></nav>';

// Schema files: the custom plainText keyword becomes a pattern.
const toJsonSchema = s => JSON.parse(JSON.stringify(s), (k, v) => {
  if (v && typeof v === 'object' && v.plainText) { const { plainText, ...rest } = v; void plainText; return { ...rest, pattern: rest.pattern || '^[^<>]*$', 'x-plain-text': true }; }
  return v;
});
mkdirSync(resolve(dir, 'schema'), { recursive: true });
writeFileSync(resolve(dir, 'schema/structure-view-spec.v1.schema.json'), JSON.stringify(toJsonSchema(Spec.schema), null, 2) + '\n');
writeFileSync(resolve(dir, 'schema/example-operations-warden.json'), JSON.stringify(Model.specs['operations-warden'], null, 2) + '\n');

// Benchmark tables from bench/results.json (median of the runs).
const bench = JSON.parse(read(resolve(dir, 'bench/results.json')));
const med = (runs, f) => { const v = runs.map(f).filter(x => x != null && !Number.isNaN(x)).sort((a, b) => a - b); return v.length ? v[Math.floor(v.length / 2)] : null; };
const fmt = (v, d = 1) => (v == null ? 'n/a' : Number(v).toFixed(d));
const cpu = name => bench.renderers.find(r => r.name === name && r.mode === 'cpu-raster');
const okRuns = r => r.runs.filter(x => !x.failed);
const gz = k => fmt(bench.sizes[k].gzipKb);
const rendererNames = { 'own-canvas2d': 'A · Own Canvas 2D engine', 'own-canvas2d-semantic': 'A · same, semantic zoom on', 'pixi-webgl': 'B · PixiJS 8 (WebGL 2D)', cytoscape: 'C · Cytoscape.js 3 (compound graph)' };
const sizeKey = name => (name === 'own-canvas2d-semantic' ? 'own-canvas2d' : name);
function rendererTable(full) {
  const rows = bench.renderers.filter(r => r.mode === 'cpu-raster').map(r => {
    const ok = okRuns(r), sz = bench.sizes[sizeKey(r.name)];
    const gpu = bench.renderers.find(x => x.name === r.name && x.mode === 'gpu-default');
    const cells = [rendererNames[r.name], `${fmt(sz.gzipKb)} kB` + (full ? ` <span class="dim">(${fmt(sz.rawKb)} raw)</span>` : ''),
      `${fmt(med(ok, x => x.panZoom.renderFps), 0)}` + (full ? ` <span class="dim">p95 ${fmt(med(ok, x => x.panZoom.frameP95), 0)} ms</span>` : '')];
    if (full) cells.push(fmt(med(gpu ? okRuns(gpu) : [], x => x.panZoom.renderFps), 0));
    cells.push(`${fmt(med(ok, x => x.idle.browserCpuPct))} %`, `${fmt(med(ok, x => x.flow.browserCpuPct), 0)} %` + (full ? ` <span class="dim">at ${fmt(med(ok, x => x.flow.framesPerSec), 0)} fps</span>` : ''));
    if (full) cells.push(`${fmt(med(ok, x => x.heapMb))} MB`, `${fmt(med(ok, x => x.init.firstRenderMs), 0)} ms`);
    return '<tr><td>' + cells.join('</td><td>') + '</td></tr>';
  });
  const head = full
    ? ['Candidate', 'Lazy chunk, gzip', 'Pan and zoom, frames a second (CPU raster)', 'Same, default GPU path', 'Idle CPU', 'Flow animation CPU', 'JS heap', 'First render']
    : ['Candidate', 'Chunk, gzip', 'Pan and zoom, frames a second', 'Idle CPU', 'Flow CPU'];
  return `<div class="tablewrap${full ? ' media-breakout' : ''}"><table${full ? ' class="bench"' : ''}><thead><tr><th>${head.join('</th><th>')}</th></tr></thead><tbody>\n${rows.join('\n')}\n</tbody></table></div>`;
}
const chartNames = { uplot: 'uPlot 1', echarts: 'Apache ECharts 6', chartjs: 'Chart.js 4' };
const chartParts = { uplot: '', echarts: ' (line, bar, grid, tooltip, legend, aria, canvas)', chartjs: ' (line, bar, scales, tooltip, legend, filler)' };
const chartLicence = { uplot: 'MIT', echarts: 'Apache-2.0', chartjs: 'MIT' };
function chartTable(full) {
  const head = full ? ['Library', 'Lazy chunk, gzip', 'Three charts, first render', 'Live update of 6 × 1,440 points', 'JS heap', 'Licence'] : ['Library', 'Chunk, gzip', 'First render', 'Live update', 'Licence'];
  return `<div class="tablewrap${full ? ' media-breakout' : ''}"><table${full ? ' class="bench"' : ''}><thead><tr><th>${head.join('</th><th>')}</th></tr></thead><tbody>\n`
    + bench.charts.map(c => '<tr><td>' + [chartNames[c.name] + (full ? chartParts[c.name] : ''), `${gz(c.name)} kB` + (full ? ` <span class="dim">(${fmt(bench.sizes[c.name].rawKb)} raw)</span>` : ''),
      `${fmt(med(c.runs, x => x.init.firstRenderMs), 0)} ms`, `${fmt(med(c.runs, x => x.init.updateMs))} ms`, ...(full ? [`${fmt(med(c.runs, x => x.heapMb))} MB`] : []), chartLicence[c.name]].join('</td><td>') + '</td></tr>').join('\n')
    + '\n</tbody></table></div>';
}
function benchEnv() {
  const e = bench.environment;
  return `Measured ${bench.generatedAt.slice(0, 10)} on the task runner: ${esc(e.cpu)}, ${e.cores} threads, headless Chromium ${e.chromium}, ${e.viewport}. WebGL there is ${esc(e.webgl)}: the runner has no usable GPU. Median of ${bench.renderers[0].runs.length} runs. Versions: ${Object.entries(e.versions).map(([k, v]) => k + ' ' + v).join(', ')}.`;
}
function inventoryTable() {
  const cls = { must: 'Must back up', escrow: 'Must escrow', reproducible: 'Reproducible', cache: 'Cache' };
  return '<div class="tablewrap media-breakout"><table><thead><tr><th>Data location</th><th>Where</th><th>Size</th><th>Class</th><th>Copies today</th><th>Recovery point</th><th>Evidence</th></tr></thead><tbody>\n'
    + Model.dataLocations.map(l => {
      const s = Layers.locStatus(l);
      const copies = l.copies.length ? l.copies.map(c => ({ 'git-remote': 'git remote, ' + (c.at ? 'last push ' + c.at.slice(11, 16) : ''), 'local-snapshot': 'manual snapshot ' + (c.at || '').slice(0, 10) + ', same disk', origin: 'project remotes', registry: 'registry' }[c.kind] || c.kind)).join('; ') : 'none';
      const rpo = s === 'ok' ? 'minutes' : s === 'local' ? '8 days, same disk' : s === 'gap' ? 'none' : 'rebuild';
      const node = Model.nodes.find(n => n.id === l.node);
      return `<tr data-status="${s}"><td><b>${esc(l.label)}</b><br><span class="dim">${esc(l.detail)}</span></td><td>${esc(node.label)}</td><td>${Layers.gb(l.sizeGb)}</td><td>${cls[l.cls]}</td><td class="st-${s}">${esc(copies)}</td><td>${rpo}</td><td><span class="dim">${esc(l.source || 'illustrative')}</span></td></tr>`;
    }).join('\n') + '\n</tbody></table></div>';
}

const wb = JSON.parse(read(resolve(dir, 'workbench.json')));
function decisionsHtml() {
  return wb.openDecisions.map(d => `<div class="decision" id="${d.id}" data-decision-id="${d.decisionId}" data-decision-kind="single">
<h3>${d.id} · ${esc(d.title)}</h3><p>${esc(d.question)}</p>
${Object.entries(d.options).map(([k, v]) => `<label data-option-id="${d.decisionId}-${k.toLowerCase()}"><input type="radio" name="${d.id}"><span><b>${k}</b> · ${esc(v)}${k === d.recommended ? '<span class="rec">Recommended</span>' : ''}</span></label>`).join('\n')}
<p class="dim">${esc(d.rationale)}</p><div data-comment="Note (optional)"></div>
</div>`).join('\n');
}
function slicesHtml() {
  return '<div class="tablewrap"><table><thead><tr><th>Slice</th><th>Delivers</th><th>After</th></tr></thead><tbody>\n'
    + wb.implementationTasks.map(t => `<tr><td><b>${t.id}</b> ${esc(t.title)}</td><td>${esc(t.delivers)}</td><td>${t.dependsOn.length ? t.dependsOn.join(', ') : 'none'}</td></tr>`).join('\n')
    + '\n</tbody></table></div>';
}
// Capture dates come from the manifest's group of each kind, not from the text.
const manifest = JSON.parse(read(resolve(dir, 'assets/capture-manifest.json'))).groups;
const longDate = iso => new Date(iso).toLocaleDateString('en-GB', { day: 'numeric', month: 'long', year: 'numeric', timeZone: 'UTC' });
const timeOf = kind => `<time datetime="${manifest[kind].capturedAt.slice(0, 10)}">${longDate(manifest[kind].capturedAt)}</time>`;
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
const mobileGallery = () => Layers.LAYERS.flatMap(l => ['light', 'dark'].map(t => `<figure><img src="assets/layer-${l.id}-${t}-390.png" alt="${attr(l.title + ' layer at 390 px, ' + t + ' theme.')}" loading="lazy"><figcaption>${esc(l.title)}, ${t}</figcaption></figure>`)).join('\n');
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
    if (name.startsWith('today-structure-view')) return "Today's Structure view of the product, role-named API fixture";
    if (name.startsWith('replay-filmstrip')) return 'Six moments of the live replay with captions';
    if (name === 'replay-recording.webm') return 'Screen recording of the replay at speed 2, about 25 seconds';
    throw new Error('Unclassified capture ' + name);
  };
  const files = readdirSync(resolve(dir, 'assets')).filter(f => /\.(png|webm)$/.test(f)).sort();
  for (const l of Layers.LAYERS) for (const t of ['light', 'dark']) for (const w of [1440, 390]) {
    if (!files.includes(`layer-${l.id}-${t}-${w}.png`)) throw new Error(`Missing capture layer-${l.id}-${t}-${w}.png`);
  }
  const rows = files.map(f => {
    const buf = readFileSync(resolve(dir, 'assets', f));
    const px = f.endsWith('.png') ? `${buf.readUInt32BE(16)} × ${buf.readUInt32BE(20)}` : '1280 × 800, WebM';
    const viewport = f.startsWith('today-') ? '1600' : /-390\.png$/.test(f) ? '390' : '1440';
    const group = Object.keys(manifest).find(k => manifest[k].frames.includes(f));
    if (!group) throw new Error(`${f} is in assets/ but in no group of capture-manifest.json`);
    return `<tr><td><code>${f}</code></td><td>${esc(shows(f))}</td><td>${/-dark[-.]/.test(f) ? 'dark' : 'light'}</td><td>${viewport}</td><td>${px}</td><td>${manifest[group].capturedAt.slice(0, 10)}</td></tr>`;
  });
  const n = Layers.LAYERS.length;
  return `<p>${n} layers × light and dark × 1440 px and 390 px: ${n * 4} of ${n * 4} captures present. All ${files.length} files are in <code>assets/</code>.</p>
<div class="tablewrap media-breakout"><table><thead><tr><th>File</th><th>Shows</th><th>Theme</th><th>Viewport, px</th><th>Pixels</th><th>Captured</th></tr></thead><tbody>
${rows.join('\n')}
</tbody></table></div>`;
}
const real = JSON.parse(read(resolve(dir, 'assets/real-scene-metrics.json')));
const flowFps = name => fmt(med(okRuns(cpu(name)), x => x.flow.framesPerSec), 0);
const prodSources = ['engine.js', 'layers.js', 'spec.js'].map(f => read(src(f))).join('\n');

const values = {
  BENCH_TABLE: () => rendererTable(true), BENCH_BRIEF: () => rendererTable(false), CHART_TABLE: () => chartTable(true), CHART_BRIEF: () => chartTable(false), BENCH_ENV: benchEnv,
  INVENTORY: inventoryTable, DECISIONS: decisionsHtml, SLICES: slicesHtml, LAYER_FIGURES: layerFigures, MOBILE_GALLERY: mobileGallery, CAPTURES: capturesTable,
  LAYER_ROWS: () => Layers.LAYERS.map(l => `<tr><td><b>${esc(l.title)}</b></td><td>${esc(l.data)}</td><td>${esc(l.encoding)}</td></tr>`).join('\n'),
  PRODUCT_TIME: () => timeOf('product'), PRODUCT_COMMIT: () => manifest.product.commit.slice(0, 9), PROTO_TIME: () => timeOf('prototype'), FILMSTRIP_TIME: () => timeOf('filmstrip'),
  UPDATED: () => longDate(wb.updatedAt), DECISION_COUNT: () => String(wb.openDecisions.length), SLICE_COUNT: () => String(wb.implementationTasks.length),
  OWN_GZ: () => gz('own-canvas2d'), UPLOT_GZ: () => gz('uplot'), CYTO_GZ: () => gz('cytoscape'), PIXI_GZ: () => gz('pixi-webgl'), THREE_GZ: () => gz('three-3d-reference'),
  A_FLOW_FPS: () => flowFps('own-canvas2d'), C_FLOW_FPS: () => flowFps('cytoscape'),
  REAL_DATE: () => longDate(real.measuredAt),
  REAL_FPS: () => `a frame costs ${real.panFrameMsP50} ms (p95 ${real.panFrameMsP95} ms) during a scripted pan and zoom at 1440 px, ${real.panFrameMsP95 < 16.7 ? 'inside' : 'above'} the 16.7 ms of 60 frames a second`,
  REAL_IDLE: () => `${real.idleFrames} frames in ${real.idleSeconds} seconds idle; ${real.hiddenFrames} frames in ${real.idleSeconds} seconds with the tab hidden`,
  REAL_FLOW: () => `${real.flowFps} frames a second with the Performance layer on, ${real.flowCpuPct} % of one core for the page`,
  REAL_DELTAS: () => `${real.deltas} deltas over the ${real.replaySeconds}-second replay produced ${real.updates} visual updates`,
};
const REGION = /<!--g:([A-Z_]+)-->[\s\S]*?<!--\/g-->/g;
function fill(file, extra) {
  const html = read(resolve(dir, file));
  const out = html.replace(REGION, (m, key) => {
    const value = key === 'STYLE' ? styleFor(html) : key === 'NAV' ? navFor(file) : extra[key] ?? (values[key] ? values[key]() : null);
    if (value == null) throw new Error(`${file}: unknown region ${key}`);
    if (value.includes('<!--/g-->')) throw new Error(`${file}: region ${key} contains a region end`);
    return `<!--g:${key}-->${value}<!--/g-->`;
  });
  if (out.includes('—')) throw new Error(`${file}: em dash found`);
  if (/\{\{[A-Z_]+\}\}/.test(out)) throw new Error(`${file}: unresolved placeholder`);
  writeFileSync(resolve(dir, file), out);
}

// prototype.html: generated whole. Only inline scripts run in the Studio's Dossier viewer.
const scripts = ['engine.js', 'layers.js', 'spec.js', 'model.js', 'dom.js', 'app.js'].map(f => minify(read(src(f)), 'js')).join('\n').replace(/<\/script/gi, '<\\/script');
const body = read(src('prototype.body.html'));
const intro = body.slice(0, body.indexOf('<div class="lsc '));
const stage = body.slice(intro.length);
writeFileSync(resolve(dir, 'prototype.html'), `<!DOCTYPE html>
<!-- GENERATED by build.mjs. Do not edit and do not review as text: markup from src/prototype.body.html,
     styles from src/canvas.css, script from src/engine.js, layers.js, spec.js, model.js, dom.js and app.js, minified.
     It is one file because the Studio's Dossier viewer runs inline scripts only. -->
<html lang="en" data-document-pattern="concept">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Live prototype | Live structure canvas</title>
${styleFor(intro, read(src('canvas.css')))}
</head>
<body>
<main class="article">
${intro.trim().replace('</header>', '</header>\n' + navFor('prototype.html'))}
${stage.trim()}
</main>
<script>
${scripts}
</script>
</body>
</html>
`);

// The pages state what the review receives; the statement is part of a page, so
// write, measure and repeat until the numbers stand.
const root = resolve(dir, '../..');
const entry = 'docs/live-structure-canvas/index.html';
const n = v => v.toLocaleString('en-US');
let snapshot, diff, facts = { REVIEW_FACTS: 'measured by the build' };
for (let i = 0; i < 6; i++) {
  for (const [file] of PAGES) if (file !== 'prototype.html') fill(file, facts);
  snapshot = reviewSnapshot(root, entry);
  diff = reviewDiff(root, 'docs/live-structure-canvas');
  const text = diff.files.filter(f => !f.binary), largest = text.filter(f => !f.generated).sort((a, b) => b.characters - a.characters)[0];
  const next = `today ${text.length} text files, ${diff.files.length - text.length - 1} images and the recording, ${n(diff.lines)} lines; the largest authored file is <code>${largest.path.split('/').slice(2).join('/')}</code> with ${n(largest.characters)} characters as a diff; `
    + `the linked files are ${snapshot.inspected} with ${n(snapshot.textCharacters)} characters of text`;
  if (next === facts.REVIEW_FACTS) break;
  facts = { REVIEW_FACTS: next };
}
const own = 'docs/live-structure-canvas/';
const foreign = snapshot.files.filter(f => !f.path.startsWith(own)).map(f => f.path + ': a linked file outside this Dossier takes review budget.');
const problems = [...snapshot.problems, ...diff.problems, ...foreign];
if (problems.length) throw new Error('The concept review would not receive the whole Dossier:\n' + problems.join('\n'));
console.log(JSON.stringify({
  pages: Object.fromEntries(diff.files.filter(f => f.path.endsWith('.html')).map(f => [f.path.slice(own.length), f.characters])),
  canvasChunkGzipKb: +(gzipSync(Buffer.from(minify(prodSources, 'js'))).length / 1024).toFixed(1),
  reviewDiff: { files: diff.changedFiles, fileLimit: LIMITS.diffFiles, lines: diff.lines, lineLimit: LIMITS.diffLines, characters: diff.characters, characterLimit: LIMITS.diffCharacters },
  reviewSnapshot: { files: snapshot.inspected, fileLimit: LIMITS.files, textCharacters: snapshot.textCharacters, characterLimit: LIMITS.totalCharacters },
}, null, 2));
