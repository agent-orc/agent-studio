import * as echarts from 'echarts/core';
import { LineChart, BarChart } from 'echarts/charts';
import { GridComponent, TooltipComponent, LegendComponent, AriaComponent } from 'echarts/components';
import { CanvasRenderer } from 'echarts/renderers';
import { installChart, COLORS, appendSample } from './chart-data.js';

echarts.use([LineChart, BarChart, GridComponent, TooltipComponent, LegendComponent, AriaComponent, CanvasRenderer]);
let day;
const opt = d => ({ animation: false, aria: { enabled: true }, color: COLORS, tooltip: { trigger: 'axis' }, legend: {}, xAxis: { type: 'time' }, yAxis: { type: 'value' },
  series: d.six.map((s, i) => ({ type: 'line', name: 'Series ' + (i + 1), showSymbol: false, data: d.day.map((t, j) => [t * 1000, s[j]]) })) });
installChart('echarts', {
  init(els, d) {
    day = echarts.init(els[0]); day.setOption(opt(d));
    echarts.init(els[1]).setOption({ animation: false, aria: { enabled: true }, color: COLORS, tooltip: {}, legend: {}, xAxis: { type: 'category', data: d.days }, yAxis: {}, series: d.stacked.map((s, j) => ({ type: 'bar', stack: 'all', name: 'Kind ' + (j + 1), data: s })) });
    echarts.init(els[2]).setOption({ animation: false, aria: { enabled: true }, color: COLORS, xAxis: { type: 'time' }, yAxis: {}, series: [{ type: 'line', showSymbol: false, areaStyle: {}, data: d.week.map((t, j) => [t * 1000, d.weekVals[j]]) }] });
  },
  append(d, i) { appendSample(d, i); day.setOption({ series: opt(d).series }); },
});
