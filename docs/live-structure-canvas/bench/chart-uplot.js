import uPlot from 'uplot';
import 'uplot/dist/uPlot.min.css';
import { installChart, COLORS, appendSample } from './chart-data.js';

let day;
installChart('uplot', {
  init(els, d) {
    day = new uPlot({ width: 640, height: 260, series: [{}, ...d.six.map((_, i) => ({ label: 'Series ' + (i + 1), stroke: COLORS[i], width: 2 }))] }, [d.day, ...d.six], els[0]);
    // Stacked bars: cumulative sums, the largest drawn first.
    const acc = d.stacked.map((_, j) => d.days.map((_, i) => d.stacked.slice(0, j + 1).reduce((s, r) => s + r[i], 0)));
    const bars = uPlot.paths.bars({ size: [0.7, 40] });
    const rev = acc.map((s, j) => ({ s, j })).reverse();
    new uPlot({ width: 640, height: 260, series: [{}, ...rev.map(({ j }) => ({ label: 'Kind ' + (j + 1), fill: COLORS[j], stroke: COLORS[j], paths: bars }))] }, [d.days, ...rev.map(r => r.s)], els[1]);
    new uPlot({ width: 640, height: 260, series: [{}, { label: 'Load', stroke: COLORS[0], fill: 'rgba(59,130,246,0.12)', width: 2 }] }, [d.week, d.weekVals], els[2]);
  },
  append(d, i) { appendSample(d, i); day.setData([d.day, ...d.six]); },
});
