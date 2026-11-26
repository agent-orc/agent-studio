// Chart colours come from the Studio tokens on the stage element; nothing here names a colour.
// Canvas needs plain values, so mixes are computed here instead of with color-mix().
const parse = c => {
  c = c.trim();
  if (c[0] === '#') { const h = c.length === 4 ? c.slice(1).replace(/./g, x => x + x) : c.slice(1); return [0, 2, 4].map(i => parseInt(h.slice(i, i + 2), 16)).concat(1); }
  const m = c.match(/[\d.]+/g).map(Number); return [m[0], m[1], m[2], m[3] ?? 1];
};
export const mix = (a, b, t) => { const x = parse(a), y = parse(b); return `rgb(${[0, 1, 2].map(i => Math.round(x[i] + (y[i] - x[i]) * t)).join(',')})`; };
export const alpha = (a, t) => { const x = parse(a); return `rgba(${x[0]},${x[1]},${x[2]},${t})`; };
export function readTheme(el) {
  const s = getComputedStyle(el), v = n => s.getPropertyValue('--studio-' + n).trim();
  const ground = v('bg-editor'), ink = v('fg-strong'), pending = v('accent-4-strong');
  const over = c => { const x = parse(c), g = parse(ground); return `rgb(${[0, 1, 2].map(i => Math.round(g[i] + (x[i] - g[i]) * x[3])).join(',')})`; };
  return {
    ground, panel: v('bg-sidebar'), raised: v('bg-elevated'), ink, text: v('fg'), dim: v('fg-dim'), muted: v('fg-muted'),
    line: over(v('border')), lineStrong: over(v('border-strong')), ok: v('accent-success-strong'), warn: v('accent-warn-strong'), bad: v('accent-6-strong'),
    info: v('accent-3-strong'), pending, assess: v('accent-teal'), brand: v('accent'),
    // Sequential ramp for "releases behind": one hue, the pending violet, from near the ground to full.
    ramp: [0.14, 0.3, 0.5, 0.72, 1].map(t => mix(ground, pending, t)),
    outcome: [v('accent-success-strong'), v('accent-6-strong'), v('accent-warn-strong'), v('fg-muted'), v('accent-3-strong')],
    font: s.getPropertyValue('--font-ui').trim() || 'system-ui, sans-serif', dark: parse(ground)[0] < 128,
  };
}
