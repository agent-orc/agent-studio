// The opt-in 3D view, drawn with Canvas 2D: no WebGL and no extra library.
// Deploy landscape: weeks by projects, bar height = deploy runs in that week,
// colour = share of those runs that failed or were rolled back. Orthographic,
// so equal heights look equal wherever a bar stands; "flatten" lays the scene
// down into the 2D heat map it is made of.
//   mount3d(el, land, ctx) -> { rotate(dYaw), flatten(on), update(land), dispose() }
import { mix } from './theme.js';
import { GROUPS } from './data.js';

export function mount3d(el, land, ctx) {
  ctx = { readout() {}, groups: GROUPS, ...ctx };
  const cv = document.createElement('canvas'); cv.tabIndex = 0; cv.setAttribute('role', 'img'); el.appendChild(cv);
  const g = cv.getContext('2d');
  let yaw = -0.62, pitch = 0.62, lift = 1, width = 0, height = 0, hover = -1, raf = 0, tops = [], anim = null, frames = 0;
  const reduced = () => ctx.animate === false || matchMedia('(prefers-reduced-motion: reduce)').matches;
  function draw() {
    raf = 0; frames++; if (!width) return;
    const t = ctx.theme, W = land.weeks, P = land.projects.length, r = Math.min(2, devicePixelRatio || 1);
    cv.width = width * r; cv.height = height * r; cv.style.width = width + 'px'; cv.style.height = height + 'px'; g.setTransform(r, 0, 0, r, 0, 0); g.clearRect(0, 0, width, height);
    const cy = Math.cos(yaw), sy = Math.sin(yaw), cp = Math.cos(pitch), sp = Math.sin(pitch);
    const span = Math.abs(cy) * W + Math.abs(sy) * P + 2, depth = Math.abs(sy) * W + Math.abs(cy) * P, zMax = P * 0.34 * lift;
    const s = Math.min((width - 60) / span, (height - 64) / (depth * sp + zMax * cp + 1)), ox = width / 2, oy = height / 2 + (zMax * cp * s) / 2 + 6;
    // world: x = week, y = project, z = up; centred, rotated about z, tilted by pitch
    const proj = (x, y, z) => { const a = x - W / 2, b = y - P / 2; return [ox + (a * cy - b * sy) * s, oy + (a * sy + b * cy) * sp * s - z * cp * s]; };
    const quad = (pts, fill) => { g.beginPath(); g.moveTo(pts[0][0], pts[0][1]); for (let i = 1; i < pts.length; i++) g.lineTo(pts[i][0], pts[i][1]); g.closePath(); g.fillStyle = fill; g.fill(); };
    // floor with week and group rules
    quad([proj(0, 0, 0), proj(W, 0, 0), proj(W, P, 0), proj(0, P, 0)], t.panel);
    g.strokeStyle = t.line; g.lineWidth = 1; g.beginPath();
    for (let w = 0; w <= W; w += 4) { const a = proj(w, 0, 0), b = proj(w, P, 0); g.moveTo(a[0], a[1]); g.lineTo(b[0], b[1]); }
    for (let p = 0; p <= P; p++) if (p === 0 || p === P || land.groups[p] !== land.groups[p - 1]) { const a = proj(0, p, 0), b = proj(W, p, 0); g.moveTo(a[0], a[1]); g.lineTo(b[0], b[1]); }
    g.stroke();
    // painter's order: far cells first along both axes
    const wOrder = Array.from({ length: W }, (_, i) => i), pOrder = Array.from({ length: P }, (_, i) => i);
    if (sy < 0) wOrder.reverse(); if (cy < 0) pOrder.reverse();
    const low = mix(t.ground, t.dim, 0.55), pad = 0.14; tops = [];
    const sideA = sy >= 0 ? 1 : 0, sideB = cy >= 0 ? 1 : 0; // which x face and which y face look at the viewer
    for (const w of wOrder) for (const p of pOrder) {
      const i = w * P + p, n = land.count[i]; if (!n) continue;
      const z = (n / land.max) * zMax + 0.02, x0 = w + pad, x1 = w + 1 - pad, y0 = p + pad, y1 = p + 1 - pad, col = mix(low, t.bad, Math.min(1, land.fail[i] * 1.6));
      if (lift > 0.02) {
        const xs = sideA ? x1 : x0, ys = sideB ? y1 : y0;
        quad([proj(xs, y0, 0), proj(xs, y1, 0), proj(xs, y1, z), proj(xs, y0, z)], mix(col, t.dark ? '#000000' : t.ink, t.dark ? 0.45 : 0.3));
        quad([proj(x0, ys, 0), proj(x1, ys, 0), proj(x1, ys, z), proj(x0, ys, z)], mix(col, t.dark ? '#000000' : t.ink, t.dark ? 0.28 : 0.16));
      }
      const top = [proj(x0, y0, z), proj(x1, y0, z), proj(x1, y1, z), proj(x0, y1, z)]; quad(top, i === hover ? t.info : col); tops.push({ i, top });
    }
    // axes: months along the weeks, group names along the projects, the height scale
    g.font = `11px ${t.font}`; g.fillStyle = t.dim; g.textBaseline = 'middle';
    const frontY = cy >= 0 ? P : 0, frontX = sy >= 0 ? W : 0, off = 14;
    g.textAlign = 'center'; let lastMonth = -1;
    for (let w = 0; w < W; w++) { const d = new Date((land.t0 + w * 7 * 86400) * 1000), m = d.getUTCMonth(); if (m === lastMonth || d.getUTCDate() > 7) continue; lastMonth = m; const a = proj(w + 0.5, frontY + (cy >= 0 ? 1.6 : -1.6), 0); g.fillText(d.toLocaleDateString('en-GB', { month: 'short', timeZone: 'UTC' }), a[0], a[1] + (lift < 0.5 ? 0 : 4)); }
    g.textAlign = sy >= 0 === cy >= 0 ? 'left' : 'right';
    for (let p = 0; p < P; p++) if (p === 0 || land.groups[p] !== land.groups[p - 1]) { let q = p; while (q < P && land.groups[q] === land.groups[p]) q++; const a = proj(frontX + (sy >= 0 ? 0.8 : -0.8), (p + q) / 2, 0); g.fillText(ctx.groups[land.groups[p]], a[0] + (g.textAlign === 'left' ? off - 8 : 8 - off), a[1]); }
    if (lift > 0.5) { const backX = sy >= 0 ? 0 : W, backY = cy >= 0 ? 0 : P, a = proj(backX, backY, 0), b = proj(backX, backY, zMax); g.strokeStyle = t.dim; g.beginPath(); g.moveTo(a[0], a[1]); g.lineTo(b[0], b[1]); g.stroke(); g.textAlign = 'center'; g.fillText(land.max + ' runs a week', b[0], b[1] - 9); g.fillText('0', a[0] - 8, a[1]); }
    if (hover >= 0) ctx.readout(describe(hover));
  }
  const describe = i => { const w = Math.floor(i / land.projects.length), p = i % land.projects.length, d = new Date((land.t0 + w * 7 * 86400) * 1000); return `${land.projects[p]}, week of ${d.toLocaleDateString('en-GB', { day: 'numeric', month: 'short', timeZone: 'UTC' })}: ${land.count[i]} deploy runs, ${Math.round(land.fail[i] * 100)} % failed or rolled back`; };
  const ask = () => { if (!raf) raf = requestAnimationFrame(draw); };
  const inside = (pts, x, y) => { let c = false; for (let i = 0, j = pts.length - 1; i < pts.length; j = i++) if ((pts[i][1] > y) !== (pts[j][1] > y) && x < (pts[j][0] - pts[i][0]) * (y - pts[i][1]) / (pts[j][1] - pts[i][1]) + pts[i][0]) c = !c; return c; };
  let dragging = null;
  cv.addEventListener('pointerdown', ev => { dragging = { x: ev.clientX, y: ev.clientY, yaw, pitch }; cv.setPointerCapture(ev.pointerId); });
  cv.addEventListener('pointermove', ev => {
    if (dragging) { yaw = dragging.yaw + (ev.clientX - dragging.x) * 0.008; pitch = Math.max(0.3, Math.min(1.5708, dragging.pitch + (ev.clientY - dragging.y) * 0.006)); ask(); return; }
    const b = cv.getBoundingClientRect(), x = ev.clientX - b.left, y = ev.clientY - b.top; let h = -1;
    for (let k = tops.length - 1; k >= 0; k--) if (inside(tops[k].top, x, y)) { h = tops[k].i; break; }
    if (h !== hover) { hover = h; if (h < 0) ctx.readout(''); ask(); }
  });
  cv.addEventListener('pointerup', () => { dragging = null; });
  cv.addEventListener('keydown', ev => { const d = { ArrowLeft: [-0.12, 0], ArrowRight: [0.12, 0], ArrowUp: [0, -0.08], ArrowDown: [0, 0.08] }[ev.key]; if (d) { ev.preventDefault(); yaw += d[0]; pitch = Math.max(0.3, Math.min(1.5708, pitch + d[1])); ask(); } });
  const ro = new ResizeObserver(([e]) => { width = Math.floor(e.contentRect.width); height = Math.max(300, Math.min(520, Math.round(width * 0.56))); el.style.minHeight = height + 'px'; ask(); });
  ro.observe(el); width = Math.floor(el.clientWidth); height = Math.max(300, Math.min(520, Math.round(width * 0.56))); draw();
  function tween(to, ms) { // flatten and raise: the one animation of this view
    cancelAnimationFrame(anim); if (reduced()) { ({ lift, pitch, yaw } = to); draw(); return; }
    const from = { lift, pitch, yaw }, t0 = performance.now();
    const step = now => { const k = Math.min(1, (now - t0) / ms), e = k * k * (3 - 2 * k); lift = from.lift + (to.lift - from.lift) * e; pitch = from.pitch + (to.pitch - from.pitch) * e; yaw = from.yaw + (to.yaw - from.yaw) * e; draw(); if (k < 1) anim = requestAnimationFrame(step); };
    anim = requestAnimationFrame(step);
  }
  return {
    rotate(d) { yaw += d; draw(); }, flatten(on) { tween(on ? { lift: 0, pitch: 1.5708, yaw: 0 } : { lift: 1, pitch: 0.62, yaw: -0.62 }, 420); },
    update(l) { land = l; hover = -1; ask(); }, retheme(t) { ctx.theme = t; ask(); }, frames: () => frames, dispose() { ro.disconnect(); cancelAnimationFrame(raf); cancelAnimationFrame(anim); cv.remove(); },
  };
}
