import { Chart, LineController, BarController, LineElement, BarElement, PointElement, LinearScale, CategoryScale, Tooltip, Legend, Filler } from 'chart.js';
import { installChart, COLORS, appendSample } from './chart-data.js';

Chart.register(LineController, BarController, LineElement, BarElement, PointElement, LinearScale, CategoryScale, Tooltip, Legend, Filler);
let day;
const canvasIn = el => { const c = document.createElement('canvas'); el.appendChild(c); return c; };
installChart('chartjs', {
  init(els, d) {
    const common = { animation: false, responsive: false, parsing: false, normalized: true, plugins: { decimation: { enabled: false } } };
    day = new Chart(canvasIn(els[0]), { type: 'line', options: { ...common, scales: { x: { type: 'linear' } }, elements: { point: { radius: 0 } } },
      data: { datasets: d.six.map((s, i) => ({ label: 'Series ' + (i + 1), borderColor: COLORS[i], borderWidth: 2, data: d.day.map((t, j) => ({ x: t, y: s[j] })) })) } });
    new Chart(canvasIn(els[1]), { type: 'bar', options: { animation: false, responsive: false, scales: { x: { stacked: true }, y: { stacked: true } } },
      data: { labels: d.days, datasets: d.stacked.map((s, j) => ({ label: 'Kind ' + (j + 1), backgroundColor: COLORS[j], data: s })) } });
    new Chart(canvasIn(els[2]), { type: 'line', options: { ...common, scales: { x: { type: 'linear' } }, elements: { point: { radius: 0 } } },
      data: { datasets: [{ label: 'Load', borderColor: COLORS[0], fill: true, backgroundColor: 'rgba(59,130,246,0.12)', data: d.week.map((t, j) => ({ x: t, y: d.weekVals[j] })) }] } });
  },
  append(d, i) { appendSample(d, i); day.data.datasets.forEach((ds, k) => { ds.data = d.day.map((t, j) => ({ x: t, y: d.six[k][j] })); }); day.update('none'); },
});
