// Measurement harness shared by every candidate: same facts (synth() of src/data.js), same
// aggregation, same scripted filter changes. A candidate entry calls install2d(mount) or install3d(mount3d).
import * as D from '../src/data.js';
import { readTheme } from '../src/theme.js';

const ALL = { group: -1, project: -1, env: -1, tenant: -1, outcome: -1, stage: -1 };
const raf = () => new Promise(r => requestAnimationFrame(r));
const painted = async () => { await raf(); await raf(); }; // the second callback runs after the first frame was presented
const $ = id => document.getElementById(id);
const stage = () => $('stage');
const ctxOf = ds => ({ ds, theme: readTheme(stage()), animate: false, onSelect() {} });
const hostsAll = () => ({ overview: $('overview'), flow: $('flow'), timeline: $('timeline'), brush: $('brush'), figures: [...document.querySelectorAll('.fig')] });
const domCount = h => [h.overview, h.flow, h.timeline, h.brush, ...h.figures].filter(Boolean).reduce((n, el) => n + el.querySelectorAll('*').length, 0);
const quant = (a, q) => { const s = [...a].sort((x, y) => x - y); return s[Math.min(s.length - 1, Math.floor(q * s.length))]; };

// Twenty filter changes, one dimension at a time, the way a user clicks through the filter row.
export function filterScript(ds) {
  const out = [], f = { ...ALL };
  for (let i = 0; i < 20; i++) {
    const step = i % 5;
    if (step === 0) f.group = (i / 5) % 6;
    else if (step === 1) f.env = (i % 4);
    else if (step === 2) f.outcome = i % 3 === 0 ? 1 : 0;
    else if (step === 3) { f.from = ds.asOf - [30, 60, 90, 14][i % 4] * D.DAY; f.to = ds.asOf; }
    else { Object.assign(f, ALL); delete f.from; delete f.to; }
    out.push({ ...f });
  }
  return out;
}

// The 3D view's facts: deploy runs a week and project, and the share that failed or was rolled back.
export function landscape(ds, f = ALL, weeks = 26) {
  if (D.landscape) return D.landscape(ds, f, weeks);
  const P = ds.projects.length, count = new Float32Array(weeks * P), bad = new Float32Array(weeks * P), R = ds.runs;
  for (let k = 0; k < R.n; k++) {
    const w = weeks - 1 - Math.floor((ds.asOf - R.start[k]) / (7 * D.DAY));
    if (w < 0 || w >= weeks) continue;
    count[w * P + R.p[k]]++; if (R.outcome[k] === 1 || R.outcome[k] === 2) bad[w * P + R.p[k]]++;
  }
  let max = 0; const fail = bad.map((b, i) => { if (count[i] > max) max = count[i]; return count[i] ? b / count[i] : 0; });
  return { weeks, projects: ds.projects.map(p => p.name), count, fail, max };
}

export function install2d(mount) {
  let ds, handle, hosts;
  window.BENCH = {
    kind: '2d',
    // Data ready to the overview alone on screen.
    async first() {
      ds = D.synth(); const vm = D.aggregate(ds, ALL); await painted();
      const t0 = performance.now(); let error = null;
      try { handle = mount({ overview: $('overview'), figures: [] }, vm, ctxOf(ds)); } catch (e) { error = String(e.message || e); }
      const sync = performance.now() - t0; await painted();
      return { firstChartMs: error ? null : +(performance.now() - t0).toFixed(1), firstChartSyncMs: +sync.toFixed(1), error };
    },
    // Data ready to all five views on screen.
    async all() {
      const s0 = performance.now(); ds = D.synth(); const s1 = performance.now(); const vm = D.aggregate(ds, ALL); const s2 = performance.now();
      hosts = hostsAll(); await painted();
      const t0 = performance.now(); handle = mount(hosts, vm, ctxOf(ds)); const sync = performance.now() - t0; await painted();
      return { allChartsMs: +(performance.now() - t0).toFixed(1), allChartsSyncMs: +sync.toFixed(1), synthMs: +(s1 - s0).toFixed(1), aggregateMs: +(s2 - s1).toFixed(1),
        domElements: domCount(hosts), canvases: stage().querySelectorAll('canvas').length, runs: vm.runs.length, tiles: vm.tiles.length, cards: vm.cards.length };
    },
    async filters() {
      const total = [], sync = [], agg = [];
      for (const f of filterScript(ds)) {
        const t0 = performance.now(); const vm = D.aggregate(ds, f); const t1 = performance.now(); handle.update(vm); const t2 = performance.now(); await painted();
        total.push(performance.now() - t0); sync.push(t2 - t0); agg.push(t1 - t0);
      }
      const r = v => +v.toFixed(1);
      return { filterMs: r(quant(total, 0.5)), filterP95Ms: r(quant(total, 0.95)), filterSyncMs: r(quant(sync, 0.5)), filterSyncP95Ms: r(quant(sync, 0.95)), aggregateMs: r(quant(agg, 0.5)), domElements: domCount(hosts) };
    },
    dispose() { handle.dispose(); return domCount(hosts); },
  };
}

export function install3d(mount3d) {
  let view;
  window.BENCH = {
    kind: '3d',
    async first() {
      const ds = D.synth(), land = landscape(ds), el = $('three'); await painted();
      const t0 = performance.now(); view = mount3d(el, land, ctxOf(ds)); const sync = performance.now() - t0; await painted();
      const g = document.createElement('canvas').getContext('webgl2'), e = g && g.getExtension('WEBGL_debug_renderer_info');
      return { firstFrameMs: +(performance.now() - t0).toFixed(1), firstFrameSyncMs: +sync.toFixed(1), bars: land.weeks * land.projects.length,
        webgl: [...el.querySelectorAll('canvas')].some(c => !c.getContext('2d')) ? (e ? g.getParameter(e.UNMASKED_RENDERER_WEBGL) : 'webgl, renderer hidden') : 'none (Canvas 2D)' };
    },
    async rotate(steps = 60) {
      const frames = [], sync = [];
      for (let i = 0; i < steps; i++) { const t0 = performance.now(); view.rotate(0.05); sync.push(performance.now() - t0); await raf(); frames.push(performance.now() - t0); }
      const mean = a => +(a.reduce((x, y) => x + y, 0) / a.length).toFixed(2);
      return { rotateFrameMs: mean(frames), rotateSyncMs: mean(sync), rotateFrameP95Ms: +quant(frames, 0.95).toFixed(1) };
    },
    dispose() { view.dispose(); },
  };
}
