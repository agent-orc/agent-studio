// 3D deploy landscape with echarts-gl 2.1.0 (bar3D on grid3D) on ECharts 6.1.0.
// Finding, recorded in results.json by run.mjs: the modular entry points (echarts-gl/charts,
// echarts-gl/components) import deep paths such as zrender/lib/core/matrix and echarts/lib/util/layout
// that the export maps of zrender 6 and echarts 6 do not list, so a bundler that honours export maps
// (esbuild, and with it the Angular build) cannot resolve them. The prebuilt bundle below works, but it
// calls require("echarts"), which the export map answers with the whole CommonJS build: nothing is
// tree-shaken, and a chart created from the ES modules (echarts/core) is a second copy of ECharts that
// does not know bar3D and draws an empty canvas without an error. So this view requires the same copy.
/* global require */
const echarts = require('echarts');
require('echarts-gl/dist/echarts-gl.min.js');

export function mount3d(el, land, ctx) {
  const t = ctx.theme, W = land.weeks, P = land.projects.length, data = [];
  for (let wk = 0; wk < W; wk++) for (let p = 0; p < P; p++) { const i = wk * P + p; data.push([wk, p, land.count[i], land.fail[i] || 0]); }
  const chart = echarts.init(el, null, { renderer: 'canvas' });
  let alpha = 28, beta = 40;
  const axis = { axisLine: { lineStyle: { color: t.line } }, axisLabel: { color: t.dim, fontSize: 10 }, splitLine: { lineStyle: { color: t.line } } };
  chart.setOption({ animation: false, tooltip: {},
    visualMap: { show: false, dimension: 3, min: 0, max: 0.5, inRange: { color: [t.ramp[2], t.bad] } },
    xAxis3D: { type: 'category', name: 'Week', data: Array.from({ length: W }, (_, i) => i + 1), ...axis }, yAxis3D: { type: 'category', name: 'Project', data: land.projects, ...axis }, zAxis3D: { type: 'value', name: 'Runs', ...axis },
    grid3D: { boxWidth: 130, boxDepth: 180, boxHeight: 50, viewControl: { projection: 'orthographic', alpha, beta, animation: false }, light: { main: { intensity: 1.2, shadow: false }, ambient: { intensity: 0.5 } }, environment: t.ground },
    series: [{ type: 'bar3D', data, shading: 'lambert', bevelSize: 0, barSize: 2.2 }] });
  return {
    rotate(d) { beta += d * 57.3; chart.setOption({ grid3D: { viewControl: { alpha, beta } } }); },
    dispose() { chart.dispose(); },
  };
}
