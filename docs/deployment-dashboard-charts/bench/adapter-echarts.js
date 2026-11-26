// Candidate B of the Dossier: Apache ECharts 6, tree-shaken echarts/core imports, canvas renderer.
// One instance per view, reused with setOption. Same contract as src/charts.js.
import * as echarts from 'echarts/core';
import { HeatmapChart, SankeyChart, ScatterChart, BarChart, LineChart } from 'echarts/charts';
import { GridComponent, TooltipComponent, VisualMapComponent, DataZoomComponent, BrushComponent, AriaComponent } from 'echarts/components';
import { CanvasRenderer } from 'echarts/renderers';
import { OUTCOME_LABEL } from '../src/data.js';

echarts.use([HeatmapChart, SankeyChart, ScatterChart, BarChart, LineChart, GridComponent, TooltipComponent, VisualMapComponent, DataZoomComponent, BrushComponent, AriaComponent, CanvasRenderer]);

export function mount(host, vm, ctx) {
  const { ds, theme: t } = ctx, R = ds.runs;
  const base = { animation: false, aria: { enabled: true }, textStyle: { fontFamily: t.font, color: t.text } };
  const axis = { axisLine: { lineStyle: { color: t.line } }, axisLabel: { color: t.dim, fontSize: 11 }, splitLine: { lineStyle: { color: t.line } }, axisTick: { show: false } };
  const tip = { backgroundColor: t.panel, borderColor: t.line, textStyle: { color: t.ink, fontSize: 12 } };
  const charts = {};
  const make = (key, el) => { if (el) charts[key] = echarts.init(el, null, { renderer: 'canvas' }); return charts[key]; };

  const overview = v => {
    const nE = v.envs.length, ok = [], bare = [], failed = [];
    v.tiles.forEach((c, k) => {
      const x = k % nE, y = Math.floor(k / nE);
      if (!c.setUp) bare.push([x, y, 0]); else { ok.push([x, y, c.drift]); if (c.lastOutcome === 1) failed.push([x, y]); }
    });
    return { ...base, grid: { left: 130, right: 16, top: 26, bottom: 8 }, tooltip: { ...tip, formatter: p => `${v.projects[p.value[1]].name}, ${v.envs[p.value[0]].name}: ${p.seriesIndex === 1 ? 'not set up' : p.value[2] + ' releases behind'}` },
      xAxis: { type: 'category', position: 'top', data: v.envs.map(e => e.name), ...axis, splitArea: { show: false } },
      yAxis: { type: 'category', inverse: true, data: v.projects.map(p => p.name), ...axis },
      visualMap: { show: false, seriesIndex: 0, min: 0, max: 8, dimension: 2, inRange: { color: t.ramp } },
      series: [{ type: 'heatmap', data: ok, itemStyle: { borderColor: t.ground, borderWidth: 2 }, label: { show: nE * v.projects.length <= 400, color: t.ink, fontSize: 10 } },
        { type: 'heatmap', data: bare, itemStyle: { color: t.ground, borderColor: t.line, borderWidth: 1, borderType: 'dashed' } },
        { type: 'scatter', data: failed, symbolSize: 7, itemStyle: { color: t.bad }, silent: true }] };
  };
  const flow = v => ({ ...base, tooltip: tip,
    series: [{ type: 'sankey', left: 8, right: 150, top: 8, bottom: 8, nodeWidth: 14, nodeGap: 14, draggable: false, emphasis: { focus: 'adjacency' },
      label: { color: t.ink, fontSize: 12, formatter: p => `${p.name}  ${p.value}` }, lineStyle: { color: 'source', opacity: 0.35, curveness: 0.5 },
      data: v.flow.nodes.filter(n => n.count > 0).map(n => ({ name: n.label, depth: n.col, itemStyle: { color: n.stuck ? t.warn : n.id === 'production' ? t.ok : t.info } })),
      links: v.flow.links.filter(l => l.count > 0).map(l => ({ source: v.flow.nodes.find(n => n.id === l.s).label, target: v.flow.nodes.find(n => n.id === l.t).label, value: l.count })) }] });
  const timeline = v => {
    const by = [[], [], [], [], []];
    for (const k of v.runs) by[R.outcome[k]].push([R.start[k] * 1000, +(R.dur[k] / 60).toFixed(2), k]);
    return { ...base, grid: { left: 46, right: 16, top: 12, bottom: 26 }, tooltip: { ...tip, formatter: p => `${ds.projects[R.p[p.value[2]]].name}, ${ds.envs[R.e[p.value[2]]]}: ${p.value[1]} min, ${p.seriesName}` },
      xAxis: { type: 'time', min: v.from * 1000, max: v.to * 1000, ...axis }, yAxis: { type: 'value', name: 'Duration (min)', nameTextStyle: { color: t.dim }, ...axis },
      dataZoom: [{ type: 'inside', xAxisIndex: 0 }],
      series: by.map((d, o) => ({ type: 'scatter', name: OUTCOME_LABEL[o] || 'Running', data: d, symbolSize: o === 0 ? 4 : 7, itemStyle: { color: t.outcome[o], opacity: o === 0 ? 0.55 : 0.95 } })) };
  };
  const brush = v => ({ ...base, grid: { left: 46, right: 16, top: 4, bottom: 20 }, tooltip: { ...tip, trigger: 'axis' },
    brush: { xAxisIndex: 0, brushType: 'lineX', brushMode: 'single', toolbox: [], brushStyle: { color: t.info, opacity: 0.18, borderColor: t.info } },
    xAxis: { type: 'category', data: Array.from(v.day.x, s => new Date(s * 1000).toISOString().slice(5, 10)), ...axis }, yAxis: { type: 'value', ...axis, splitNumber: 2 },
    series: v.day.counts.map((c, o) => ({ type: 'bar', stack: 'runs', name: OUTCOME_LABEL[o], data: Array.from(c), itemStyle: { color: t.outcome[o] }, barCategoryGap: '20%' })) });
  const spark = f => ({ ...base, grid: { left: 2, right: 2, top: 4, bottom: 4 }, xAxis: { type: 'category', show: false, data: f.series.map((_, i) => i) }, yAxis: { type: 'value', show: false, scale: true },
    series: [{ type: 'line', data: f.series, showSymbol: false, connectNulls: true, lineStyle: { color: t.info, width: 2 }, areaStyle: { color: t.info, opacity: 0.1 } }] });

  // Merge mode: the instance, its components and its series are kept; only data and axis categories change.
  const draw = (v, first) => {
    charts.overview?.setOption(overview(v));
    charts.flow?.setOption(flow(v));
    charts.timeline?.setOption(timeline(v));
    if (charts.brush) {
      charts.brush.setOption(brush(v));
      if (first) charts.brush.dispatchAction({ type: 'takeGlobalCursor', key: 'brush', brushOption: { brushType: 'lineX', brushMode: 'single' } });
    }
    v.figures.forEach((f, i) => charts['fig' + i]?.setOption(spark(f)));
  };
  let current = vm;
  make('overview', host.overview)?.on('click', p => ctx.onSelect?.('project', current.projects[p.value[1]].i));
  make('flow', host.flow)?.on('click', p => { if (p.dataType === 'node') ctx.onSelect?.('stage', current.flow.nodes.find(n => n.label === p.name)?.id); });
  make('timeline', host.timeline)?.on('click', p => ctx.onSelect?.('run', p.value[2]));
  make('brush', host.brush)?.on('brushEnd', e => { const r = e.areas[0]?.coordRange; if (r) ctx.onSelect?.('range', [current.day.x[Math.max(0, r[0])], current.day.x[Math.min(current.day.x.length - 1, r[1])] + 86400]); });
  (host.figures || []).forEach((el, i) => make('fig' + i, el));
  draw(vm, true);
  return {
    update(v) { current = v; draw(v); },
    dispose() { for (const c of Object.values(charts)) c.dispose(); },
  };
}
