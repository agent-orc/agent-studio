// Dashboard data for the chart library benchmark: a day of one-minute
// samples for six series, thirty days of three stacked counts, and a week
// of one-minute samples for one series. Deterministic.
export function chartData() {
  let s = 3950;
  const rnd = () => { s = (s * 1664525 + 1013904223) >>> 0; return s / 4294967296; };
  const t0 = Date.UTC(2026, 10, 16) / 1000;
  const day = Array.from({ length: 1440 }, (_, i) => t0 + i * 60);
  const six = Array.from({ length: 6 }, (_, j) => day.map((_, i) => 30 + 20 * Math.sin(i / 90 + j) + rnd() * 8));
  const days = Array.from({ length: 30 }, (_, i) => t0 - (29 - i) * 86400);
  const stacked = Array.from({ length: 3 }, () => days.map(() => Math.round(rnd() * 12)));
  const week = Array.from({ length: 10080 }, (_, i) => t0 - 7 * 86400 + i * 60);
  const weekVals = week.map((_, i) => 50 + 30 * Math.sin(i / 700) + rnd() * 10);
  return { day, six, days, stacked, week, weekVals };
}
export const COLORS = ['#3b82f6', '#d97757', '#6d28d9', '#5f5e59', '#18753a', '#8b6100'];
export function installChart(name, lib) {
  window.BENCH = {
    name,
    async init() {
      const d = chartData();
      const host = document.getElementById('host');
      const els = [0, 1, 2].map(() => { const e = document.createElement('div'); e.style.width = '640px'; e.style.height = '260px'; host.appendChild(e); return e; });
      const t0 = performance.now();
      await lib.init(els, d);
      await new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)));
      const first = performance.now() - t0;
      // Live update: append one sample to each series of the day chart, twenty times.
      const u0 = performance.now();
      for (let i = 0; i < 20; i++) lib.append(d, i);
      await new Promise(r => requestAnimationFrame(r));
      return { firstRenderMs: +first.toFixed(1), updateMs: +((performance.now() - u0) / 20).toFixed(2) };
    },
  };
}
export function appendSample(d, i) {
  d.day.push(d.day[d.day.length - 1] + 60); d.day.shift();
  for (const s of d.six) { s.push(s[s.length - 1] + Math.sin(i)); s.shift(); }
}
