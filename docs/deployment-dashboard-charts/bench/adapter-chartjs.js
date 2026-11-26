// Candidate C: Chart.js 4 with the matrix, sankey and zoom plugins. Same contract as src/charts.js.
import { Chart, LinearScale, CategoryScale, ScatterController, BarController, LineController, PointElement, BarElement, LineElement, Tooltip, Filler } from 'chart.js';
import { MatrixController, MatrixElement } from 'chartjs-chart-matrix';
import { SankeyController, Flow } from 'chartjs-chart-sankey';
import zoomPlugin from 'chartjs-plugin-zoom';
import { OUTCOME_LABEL } from '../src/data.js';

Chart.register(LinearScale, CategoryScale, ScatterController, BarController, LineController, PointElement, BarElement, LineElement, Tooltip, Filler, MatrixController, MatrixElement, SankeyController, Flow, zoomPlugin);

export function mount(host, vm, ctx) {
  const { ds, theme: t } = ctx, R = ds.runs, charts = {};
  Chart.defaults.animation = false; Chart.defaults.font.family = t.font; Chart.defaults.color = t.dim; Chart.defaults.borderColor = t.line;
  const canvas = el => { const c = document.createElement('canvas'); el.appendChild(c); return c; };
  const common = { responsive: true, maintainAspectRatio: false, animation: false, plugins: { legend: { display: false } } };
  const day = s => new Date(s * 1000).toISOString().slice(5, 10);
  const rampOf = d => t.ramp[Math.min(4, Math.round(d / 2))];
  let current = vm;

  const overviewData = v => {
    const nE = v.envs.length;
    return { datasets: [{ label: 'Releases behind', data: v.tiles.map((c, k) => ({ x: v.envs[k % nE].name, y: v.projects[Math.floor(k / nE)].name, v: c.setUp ? c.drift : -1, failed: c.lastOutcome === 1 })),
      backgroundColor: c => (c.raw?.v < 0 ? t.ground : rampOf(c.raw?.v || 0)), borderColor: c => (c.raw?.failed ? t.bad : c.raw?.v < 0 ? t.line : t.ground), borderWidth: c => (c.raw?.failed ? 2 : 1),
      width: c => ((c.chart.chartArea || {}).width || 0) / nE - 3, height: c => ((c.chart.chartArea || {}).height || 0) / v.projects.length - 2 }] };
  };
  const overviewScales = v => ({ x: { type: 'category', labels: v.envs.map(e => e.name), position: 'top', offset: true, grid: { display: false } }, y: { type: 'category', labels: v.projects.map(p => p.name), offset: true, reverse: true, grid: { display: false } } });
  const flowData = v => ({ datasets: [{ label: 'Cards', data: v.flow.links.filter(l => l.count > 0).map(l => ({ from: l.s, to: l.t, flow: l.count })),
    labels: Object.fromEntries(v.flow.nodes.map(n => [n.id, `${n.label}  ${n.count}`])), column: Object.fromEntries(v.flow.nodes.map(n => [n.id, n.col])),
    colorFrom: c => (v.flow.nodes.find(n => n.id === c.raw?.from)?.stuck ? t.warn : t.info), colorTo: c => { const n = v.flow.nodes.find(x => x.id === c.raw?.to); return n?.stuck ? t.warn : n?.id === 'production' ? t.ok : t.info; },
    colorMode: 'gradient', color: t.ink, nodeWidth: 14 }] });
  const timelineData = v => {
    const by = [[], [], [], [], []];
    for (const k of v.runs) by[R.outcome[k]].push({ x: R.start[k], y: R.dur[k] / 60, k });
    return { datasets: by.map((d, o) => ({ label: OUTCOME_LABEL[o] || 'Running', data: d, parsing: false, pointRadius: o === 0 ? 2 : 3.5, backgroundColor: t.outcome[o], borderWidth: 0 })) };
  };
  const brushData = v => ({ labels: Array.from(v.day.x, day), datasets: v.day.counts.map((c, o) => ({ label: OUTCOME_LABEL[o], data: Array.from(c), backgroundColor: t.outcome[o], stack: 'runs' })) });
  const sparkData = f => ({ labels: f.series.map((_, i) => i), datasets: [{ data: f.series, borderColor: t.info, borderWidth: 2, pointRadius: 0, spanGaps: true, fill: true, backgroundColor: t.info + '1a' }] });

  if (host.overview) charts.overview = new Chart(canvas(host.overview), { type: 'matrix', data: overviewData(vm), options: { ...common, scales: overviewScales(vm),
    onClick: (e, els) => { if (els[0]) ctx.onSelect?.('project', current.projects[Math.floor(els[0].index / current.envs.length)].i); },
    plugins: { legend: { display: false }, tooltip: { callbacks: { title: () => '', label: c => `${c.raw.y}, ${c.raw.x}: ${c.raw.v < 0 ? 'not set up' : c.raw.v + ' releases behind'}` } } } } });
  if (host.flow) charts.flow = new Chart(canvas(host.flow), { type: 'sankey', data: flowData(vm), options: { ...common, onClick: (e, els) => { if (els[0]) ctx.onSelect?.('stage', els[0].element?.$context?.raw?.to); } } });
  if (host.timeline) charts.timeline = new Chart(canvas(host.timeline), { type: 'scatter', data: timelineData(vm), options: { ...common,
    scales: { x: { type: 'linear', min: vm.from, max: vm.to, ticks: { callback: v => day(v), maxTicksLimit: 10 } }, y: { type: 'linear', beginAtZero: true, title: { display: true, text: 'Duration (min)' } } },
    onClick: (e, els) => { if (els[0]) ctx.onSelect?.('run', els[0].element.$context.raw.k); },
    plugins: { legend: { display: false }, tooltip: { callbacks: { label: c => `${ds.projects[R.p[c.raw.k]].name}, ${ds.envs[R.e[c.raw.k]]}: ${c.raw.y.toFixed(1)} min, ${c.dataset.label}` } },
      zoom: { zoom: { wheel: { enabled: true }, pinch: { enabled: true }, mode: 'x' }, pan: { enabled: true, mode: 'x' } } } } });
  if (host.brush) charts.brush = new Chart(canvas(host.brush), { type: 'bar', data: brushData(vm), options: { ...common, scales: { x: { stacked: true, grid: { display: false }, ticks: { maxTicksLimit: 12 } }, y: { stacked: true, ticks: { maxTicksLimit: 3 } } },
    plugins: { legend: { display: false }, tooltip: { mode: 'index', intersect: false },
      zoom: { zoom: { drag: { enabled: true, backgroundColor: t.info + '2e', borderColor: t.info, borderWidth: 1 }, mode: 'x',
        onZoomComplete: ({ chart }) => { const s = chart.scales.x; ctx.onSelect?.('range', [current.day.x[Math.max(0, Math.round(s.min))], current.day.x[Math.min(current.day.x.length - 1, Math.round(s.max))] + 86400]); } } } } } });
  (host.figures || []).forEach((el, i) => { charts['fig' + i] = new Chart(canvas(el), { type: 'line', data: sparkData(vm.figures[i]), options: { ...common, scales: { x: { display: false }, y: { display: false } }, plugins: { legend: { display: false }, tooltip: { enabled: false } } } }); });

  return {
    update(v) {
      current = v;
      if (charts.overview) { charts.overview.data = overviewData(v); charts.overview.options.scales = overviewScales(v); charts.overview.update('none'); }
      if (charts.flow) { charts.flow.data = flowData(v); charts.flow.update('none'); }
      if (charts.timeline) { charts.timeline.data = timelineData(v); charts.timeline.options.scales.x.min = v.from; charts.timeline.options.scales.x.max = v.to; charts.timeline.update('none'); }
      if (charts.brush) { charts.brush.data = brushData(v); charts.brush.update('none'); }
      v.figures.forEach((f, i) => { const c = charts['fig' + i]; if (c) { c.data = sparkData(f); c.update('none'); } });
    },
    dispose() { for (const c of Object.values(charts)) c.destroy(); },
  };
}
