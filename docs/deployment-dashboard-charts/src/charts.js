// The 2D chart chunk of the prototype: candidate B, uPlot for the two time
// charts and three small own forms (tile map and sparklines on Canvas 2D, the
// release flow as SVG). Contract shared with the bench adapters:
//   mount(host, vm, ctx) -> { update(vm), annotate(marks), dispose() }
import uPlot from 'uplot';
import { ENVS, GROUPS, OUTCOME_LABEL, STEPS, STAGE_LABEL, DAY } from './data.js';
import { alpha, mix } from './theme.js';

const SVG = 'http://www.w3.org/2000/svg';
const el = (tag, attrs = {}, parent, ns) => { const n = ns ? document.createElementNS(ns, tag) : document.createElement(tag); for (const k in attrs) n.setAttribute(k, attrs[k]); if (parent) parent.appendChild(n); return n; };
const fmtDur = s => (s < 90 ? Math.round(s) + ' s' : (s / 60).toFixed(1) + ' min');
const fmtDay = t => new Date(t * 1000).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', timeZone: 'UTC' });
const fmtTime = t => fmtDay(t) + ' ' + new Date(t * 1000).toISOString().slice(11, 16) + ' UTC';
const driftStep = d => (d >= 8 ? 4 : d >= 5 ? 3 : d >= 3 ? 2 : d >= 1 ? 1 : 0);
function canvasFor(host, label) {
  const cv = el('canvas', { role: 'img', 'aria-label': label }, host), g = cv.getContext('2d');
  return { cv, g, size(w, h) { const r = Math.min(2, devicePixelRatio || 1); cv.width = Math.round(w * r); cv.height = Math.round(h * r); cv.style.width = w + 'px'; cv.style.height = h + 'px'; g.setTransform(r, 0, 0, r, 0, 0); } };
}

// ---- Overview: one tile per project and environment ------------------------------------------
function tileMap(host, ctx) {
  const c = canvasFor(host, 'Overview of projects by environments'), g = c.g; c.cv.tabIndex = 0;
  let vm, rects = [], focus = -1, hover = -1, marks = [], width = 0;
  const T = () => ctx.theme;
  function glyph(x, y, o) { // last run, a shape per outcome on a ground chip so it reads on every fill
    const t = T(); g.fillStyle = t.ground; g.beginPath(); g.roundRect(x - 7, y - 7, 14, 14, 3); g.fill(); g.lineWidth = 1.8; g.lineCap = 'round';
    g.strokeStyle = g.fillStyle = t.outcome[o < 0 ? 3 : o];
    if (o === 0) { g.beginPath(); g.moveTo(x - 4, y); g.lineTo(x - 1, y + 3); g.lineTo(x + 4, y - 3); g.stroke(); }
    else if (o === 1) { g.beginPath(); g.arc(x, y, 5.5, 0, 7); g.fill(); g.strokeStyle = t.ground; g.beginPath(); g.moveTo(x - 2.4, y - 2.4); g.lineTo(x + 2.4, y + 2.4); g.moveTo(x + 2.4, y - 2.4); g.lineTo(x - 2.4, y + 2.4); g.stroke(); }
    else if (o === 2) { g.beginPath(); g.moveTo(x, y - 5); g.lineTo(x + 5.5, y + 4.5); g.lineTo(x - 5.5, y + 4.5); g.closePath(); g.fill(); }
    else if (o === 4) { g.beginPath(); g.arc(x, y, 4, 0, 7); g.stroke(); }
    else { g.beginPath(); g.moveTo(x - 4, y); g.lineTo(x + 4, y); g.stroke(); }
  }
  function draw() {
    if (!vm || !width) return;
    const t = T(), two = width >= 720 && vm.projects.length > 12, nameW = width >= 720 ? 132 : 94, rowH = 26, headH = 22, gap = 2;
    const rows = []; let lastGroup = -1;
    for (const p of vm.projects) { if (p.group !== lastGroup && vm.projects.length > 6) { rows.push({ group: p.group }); lastGroup = p.group; } rows.push({ p }); }
    let cut = rows.length;
    if (two) { cut = Math.ceil(rows.length / 2); while (cut < rows.length && !rows[cut].group && cut - Math.ceil(rows.length / 2) < 6) cut++; }
    const blocks = two ? [rows.slice(0, cut), rows.slice(cut)] : [rows], blockW = two ? (width - 24) / 2 : width;
    const nE = vm.envs.length, tw = (blockW - nameW) / nE - gap, height = headH + Math.max(...blocks.map(b => b.length)) * rowH + 4;
    c.size(width, height); g.clearRect(0, 0, width, height); rects = [];
    const byKey = new Map(vm.tiles.map((x, i) => [x.p * 8 + x.e, i]));
    g.textBaseline = 'middle';
    blocks.forEach((block, b) => {
      const x0 = b * (blockW + 24);
      g.font = `600 11px ${t.font}`; g.fillStyle = t.dim; g.textAlign = 'left';
      vm.envs.forEach((e, i) => g.fillText(tw < 60 ? e.name.slice(0, 4) : e.name, x0 + nameW + i * (tw + gap) + 2, headH / 2));
      block.forEach((row, r) => {
        const y = headH + r * rowH;
        if (row.group != null) { g.font = `600 10.5px ${t.font}`; g.fillStyle = t.muted; g.fillText(GROUPS[row.group].toUpperCase(), x0, y + rowH / 2 + 3); g.fillStyle = t.line; g.fillRect(x0 + nameW, y + rowH / 2 + 3, blockW - nameW, 1); return; }
        const p = row.p; g.font = `12px ${t.font}`; g.fillStyle = t.text; g.textAlign = 'left';
        let name = p.name; while (g.measureText(name).width > nameW - 8 && name.length > 3) name = name.slice(0, -2); g.fillText(name === p.name ? name : name + '…', x0, y + rowH / 2);
        if (!p.envs.length) { // a project without environments stays visible
          g.strokeStyle = t.lineStrong; g.lineWidth = 1; g.strokeRect(x0 + nameW + 0.5, y + 2.5, nE * (tw + gap) - gap - 1, rowH - 5);
          g.fillStyle = t.muted; g.font = `11px ${t.font}`; g.fillText(tw < 60 ? 'not set up' : 'Not set up: no environment declared', x0 + nameW + 8, y + rowH / 2);
          rects.push({ x: x0 + nameW, y: y + 2, w: nE * (tw + gap) - gap, h: rowH - 4, bare: p }); return;
        }
        vm.envs.forEach((e, i) => {
          const ti = byKey.get(p.i * 8 + e.i), tile = vm.tiles[ti], x = x0 + nameW + i * (tw + gap), h = rowH - 4, yy = y + 2;
          g.globalAlpha = tile.setUp && !tile.match ? 0.28 : 1;
          if (!tile.setUp) { g.strokeStyle = t.line; g.lineWidth = 1; g.strokeRect(x + 0.5, yy + 0.5, tw - 1, h - 1); if (tw >= 60) { g.fillStyle = t.muted; g.font = `10.5px ${t.font}`; g.fillText('not set up', x + 6, yy + h / 2); } rects.push({ x, y: yy, w: tw, h, ti }); g.globalAlpha = 1; return; }
          const step = driftStep(tile.drift);
          if (ctx.split() && tile.tenants) { const sw = tw / tile.tenants.length; tile.tenants.forEach((tn, k) => { const s = driftStep(tn.drift); g.fillStyle = s ? t.ramp[s] : t.panel; g.fillRect(x + k * sw, yy, sw - 1, h); if (tn.health) { g.fillStyle = t[['ok', 'warn', 'bad', 'muted'][tn.health]]; g.fillRect(x + k * sw, yy + h - 3, sw - 1, 3); } }); }
          else { g.fillStyle = step ? t.ramp[step] : t.panel; g.beginPath(); g.roundRect(x, yy, tw, h, 3); g.fill(); }
          glyph(x + 10, yy + h / 2, tile.lastOutcome);
          const onDark = step >= 3 && !(ctx.split() && tile.tenants); g.fillStyle = onDark ? t.ground : step ? t.ink : t.dim; g.font = `${step ? 600 : 400} 11px ${t.font}`;
          const appr = tile.approvals && tw >= 62 ? String(tile.approvals) : '', room = tw - 21 - 16 - (appr ? 26 + appr.length * 6 : 0);
          const words = tile.drift ? tile.drift + ' behind' : 'current', text = [`r${tile.head - tile.drift} · ${words}`, words, tile.drift ? String(tile.drift) : ''].find(c => g.measureText(c).width <= room) || '';
          if (!(ctx.split() && tile.tenants && tw < 118)) g.fillText(text, x + 21, yy + h / 2 + 0.5);
          let rx = x + tw - 8; // right side: health dot, then waiting approvals
          g.fillStyle = t.ground; g.beginPath(); g.arc(rx, yy + h / 2, 5.5, 0, 7); g.fill();
          g.fillStyle = g.strokeStyle = t[['ok', 'warn', 'bad', 'muted'][tile.health]]; g.lineWidth = 1.5; g.beginPath(); g.arc(rx, yy + h / 2, 3.2, 0, 7); tile.health === 3 ? g.stroke() : g.fill();
          if (appr) { const s = appr; g.font = `600 10px ${t.font}`; const w = g.measureText(s).width + 16; rx -= 8 + w; g.fillStyle = t.ground; g.beginPath(); g.roundRect(rx, yy + 3, w, h - 6, 7); g.fill(); g.strokeStyle = t.warn; g.lineWidth = 1.2; g.beginPath(); g.arc(rx + 7, yy + h / 2, 3, 0, 7); g.moveTo(rx + 7, yy + h / 2 - 1.8); g.lineTo(rx + 7, yy + h / 2); g.lineTo(rx + 8.6, yy + h / 2); g.stroke(); g.fillStyle = t.ink; g.fillText(s, rx + 12.5, yy + h / 2 + 0.5); }
          if (tile.noBackup) { g.fillStyle = t.bad; g.beginPath(); g.moveTo(x + tw - 9, yy); g.lineTo(x + tw, yy); g.lineTo(x + tw, yy + 9); g.closePath(); g.fill(); }
          if (tile.warn) { g.fillStyle = t.warn; g.beginPath(); g.moveTo(x + tw - 9, yy + h); g.lineTo(x + tw, yy + h); g.lineTo(x + tw, yy + h - 9); g.closePath(); g.fill(); }
          g.globalAlpha = 1; rects.push({ x, y: yy, w: tw, h, ti });
        });
      });
    });
    for (const m of marks) { const r = rects.find(q => q.ti != null && vm.tiles[q.ti].p === m.p && vm.tiles[q.ti].e === m.e); if (r) badge(g, t, r.x + 1, r.y + 1, m.n, r, m.strong); }
    const f = rects[focus >= 0 ? focus : hover]; if (f) { g.strokeStyle = t.info; g.lineWidth = 2; g.strokeRect(f.x - 1, f.y - 1, f.w + 2, f.h + 2); }
  }
  const describe = r => {
    if (!r) return null; if (r.bare) return { title: r.bare.name, rows: [['State', 'Not set up: no environment declared'], ['Next step', 'Declare environments in the project file']] };
    const tile = vm.tiles[r.ti], p = ctx.ds.projects[tile.p], title = `${p.name} · ${ENVS[tile.e]}`;
    if (!tile.setUp) return { title, rows: [['State', 'Environment not set up']] };
    const rows = [['Live release', `r${tile.head - tile.drift}, ${tile.drift ? tile.drift + ' releases behind r' + tile.head : 'current'}`], ['Last run', tile.lastOutcome < 0 ? 'none in 180 days' : `${OUTCOME_LABEL[tile.lastOutcome] || 'Running'}, ${tile.ageDays < 1 ? Math.round(tile.ageDays * 24) + ' h' : Math.round(tile.ageDays) + ' days'} ago`], ['Health', ['up', 'degraded', 'down', 'unknown'][tile.health]]];
    if (tile.approvals) rows.push(['Waiting approvals', tile.approvals + ' cards']);
    if (tile.tenants) rows.push(['Tenants', tile.tenants.map((x, i) => `${i + 1}: ${x.drift} behind`).join(', ')]);
    if (tile.noBackup) rows.push(['Backup', 'No verified off-host copy of this data']);
    if (tile.warn) rows.push(['Construction site', 'Open warning on the host of this environment']);
    return { title, rows };
  };
  const at = ev => { const b = c.cv.getBoundingClientRect(), x = ev.clientX - b.left, y = ev.clientY - b.top; return rects.findIndex(r => x >= r.x && x <= r.x + r.w && y >= r.y && y <= r.y + r.h); };
  const pick = i => { const r = rects[i]; if (!r) return; r.bare ? ctx.onSelect('project', r.bare.i) : ctx.onSelect('tile', { p: vm.tiles[r.ti].p, e: vm.tiles[r.ti].e }); };
  c.cv.addEventListener('pointermove', ev => { const i = at(ev); if (i !== hover) { hover = i; draw(); } ctx.tip(i < 0 ? null : describe(rects[i]), ev.clientX, ev.clientY); });
  c.cv.addEventListener('pointerleave', () => { hover = -1; draw(); ctx.tip(null); });
  c.cv.addEventListener('click', ev => pick(at(ev)));
  c.cv.addEventListener('keydown', ev => {
    const step = { ArrowRight: 1, ArrowLeft: -1, ArrowDown: vm.envs.length, ArrowUp: -vm.envs.length }[ev.key];
    if (step) { ev.preventDefault(); focus = Math.max(0, Math.min(rects.length - 1, (focus < 0 ? 0 : focus + step))); draw(); const r = rects[focus], b = c.cv.getBoundingClientRect(); ctx.tip(describe(r), b.left + r.x + r.w / 2, b.top + r.y + r.h, true); }
    else if (ev.key === 'Enter' || ev.key === ' ') { ev.preventDefault(); pick(focus); } else if (ev.key === 'Escape') { focus = -1; draw(); ctx.tip(null); }
  });
  c.cv.addEventListener('blur', () => { focus = -1; draw(); ctx.tip(null); });
  return { update(v) { vm = v; focus = -1; draw(); }, resize(w) { width = w; draw(); }, annotate(m) { marks = m.filter(x => x.view === 'overview'); draw(); }, redraw: draw };
}
function badge(g, t, x, y, n, _r, strong) { // numbered assessment mark, drawn in the assessment ink
  g.save(); g.fillStyle = t.assess; g.beginPath(); g.arc(x + 3, y + 3, 8, 0, 7); g.fill(); g.strokeStyle = t.ground; g.lineWidth = 1.5; g.stroke();
  g.fillStyle = t.ground; g.font = `700 10px ${t.font}`; g.textAlign = 'center'; g.textBaseline = 'middle'; g.fillText(String(n), x + 3, y + 3.5);
  if (strong && _r) { g.strokeStyle = t.assess; g.lineWidth = 2; g.strokeRect(_r.x - 1, _r.y - 1, _r.w + 2, _r.h + 2); } g.restore();
}

// ---- Release flow: where the cards are now, as an SVG flow with real buttons -------------------
function flowView(host, ctx) {
  const svg = el('svg', { class: 'dd-flow', role: 'group', 'aria-label': 'Release flow from Integrated over Staging to Production' }, host, SVG);
  const list = el('div', { class: 'dd-wait' }, host);
  let vm, width = 0, marks = [];
  function draw() {
    if (!vm || !width) return;
    const narrow = width < 620, w = narrow ? width : Math.max(360, width - 300), h = narrow ? 250 : 270, colX = [0, w * (narrow ? 0.33 : 0.44), w - 14], SHORT = { 'wait-staging': 'Not deployed', staging: 'Staging', held: 'Staging only', approval: 'Approval', production: 'Production' }, pad = 16, total = Math.max(1, vm.flow.nodes[0].count);
    svg.setAttribute('viewBox', `0 0 ${w} ${h}`); svg.setAttribute('width', w); svg.setAttribute('height', h); svg.replaceChildren();
    const pos = {}, used = [0, 0, 0], avail = h - 46;
    for (const n of vm.flow.nodes) { const k = vm.flow.nodes.filter(m => m.col === n.col).length, nh = Math.max(n.count ? 3 : 1, (avail - (k - 1) * pad) * n.count / total); pos[n.id] = { x: colX[n.col], y: 24 + used[n.col], h: nh, out: 0, in: 0 }; used[n.col] += nh + pad; }
    for (const l of vm.flow.links) {
      const a = pos[l.s], b = pos[l.t], th = Math.max(l.count ? 2 : 0, (avail - pad) * l.count / total), y0 = a.y + a.out, y1 = b.y + b.in; a.out += th; b.in += th;
      const x0 = a.x + 14, x1 = b.x, mx = (x0 + x1) / 2, node = vm.flow.nodes.find(n => n.id === l.t);
      const path = el('path', { d: `M${x0},${y0}C${mx},${y0} ${mx},${y1} ${x1},${y1}L${x1},${y1 + th}C${mx},${y1 + th} ${mx},${y0 + th} ${x0},${y0 + th}Z`, class: 'dd-link' + (node.stuck ? ' stuck' : node.id === 'production' ? ' live' : '') }, svg, SVG);
      el('title', {}, path, SVG).textContent = `${l.count} cards`;
    }
    for (const n of vm.flow.nodes) {
      const p = pos[n.id], gEl = el('g', { class: 'dd-node' + (n.stuck ? ' stuck' : n.id === 'production' ? ' live' : '') + (ctx.filters().stage === n.stage && n.stage != null ? ' on' : ''), tabindex: n.stage != null ? 0 : -1, role: n.stage != null ? 'button' : 'img', 'aria-label': `${n.label}: ${n.count} cards` + (n.stage != null ? '. Show these cards' : '') }, svg, SVG);
      el('rect', { x: p.x, y: p.y, width: 14, height: p.h, rx: 2 }, gEl, SVG);
      const right = n.col === 2, tx = right ? p.x - 8 : p.x + 22, ty = Math.max(12, Math.min(h - 18, p.y + Math.min(p.h / 2, 14)));
      const label = el('text', { x: tx, y: ty, 'text-anchor': right ? 'end' : 'start', class: 'dd-node-label' }, gEl, SVG); label.textContent = (narrow && SHORT[n.id]) || n.label;
      const count = el('text', { x: tx, y: ty + 15, 'text-anchor': right ? 'end' : 'start', class: 'dd-node-count' }, gEl, SVG); count.textContent = `${n.count.toLocaleString('en-GB')}${narrow ? '' : ' cards'} · ${(100 * n.count / total).toFixed(n.count / total < 0.1 ? 1 : 0)} %`;
      const m = marks.find(x => x.node === n.id);
      if (m) { const c = el('circle', { cx: p.x + 7, cy: p.y - 9, r: 8, class: 'dd-badge' }, gEl, SVG); void c; const tt = el('text', { x: p.x + 7, y: p.y - 5.5, 'text-anchor': 'middle', class: 'dd-badge-n' }, gEl, SVG); tt.textContent = m.n; }
      if (n.stage != null) { const go = () => ctx.onSelect('stage', n.stage); gEl.addEventListener('click', go); gEl.addEventListener('keydown', ev => { if (ev.key === 'Enter' || ev.key === ' ') { ev.preventDefault(); go(); } }); }
    }
    // Ranked list: the projects whose cards wait, with where they wait.
    list.replaceChildren(); const head = el('div', { class: 'dd-wait-head' }, list); head.textContent = 'Projects with cards not live yet';
    const top = vm.flow.waiting.slice(0, narrow ? 5 : 8), max = Math.max(1, ...top.map(x => x.total));
    for (const row of top) {
      const b = el('button', { type: 'button', class: 'dd-wait-row', 'aria-label': `${row.name}: ${row.total} cards not live. Filter to this project` }, list);
      el('span', { class: 'n' }, b).textContent = row.name;
      const bar = el('span', { class: 'bar' }, b); row.counts.forEach((v, i) => { if (v) el('i', { class: 's' + i, style: `width:${(100 * v / max).toFixed(1)}%`, title: `${STAGE_LABEL[i]}: ${v}` }, bar); });
      el('span', { class: 'v' }, b).textContent = row.total; b.addEventListener('click', () => ctx.onSelect('project', row.p));
    }
    if (!top.length) el('div', { class: 'dd-empty' }, list).textContent = 'Every card of this filter is live in Production.';
  }
  return { update(v) { vm = v; draw(); }, resize(w) { width = w; draw(); }, annotate(m) { marks = m.filter(x => x.view === 'flow'); draw(); }, redraw: draw };
}

// ---- Deploy timeline and brush on uPlot --------------------------------------------------------
function timeline(host, brushHost, ctx) {
  let vm, u, b, width = 0, marks = [];
  const LANE = 0.84, CAP = 900; // seconds of duration that fill a lane
  const axis = t => ({ stroke: t.dim, font: `11px ${t.font}`, grid: { stroke: t.line, width: 1 }, ticks: { stroke: t.line, width: 1, size: 4 } });
  function build() {
    const t = ctx.theme; if (u) u.destroy(); if (b) b.destroy(); if (!width) return;
    const lanes = vm.envs.length, h = Math.max(150, 44 + lanes * (width < 620 ? 46 : 58));
    u = new uPlot({ width, height: h, legend: { show: false }, padding: [8, 8, 0, 0],
      cursor: { points: { show: false }, drag: { x: true, y: false, setScale: false }, y: false },
      scales: { x: { time: true }, y: { range: [0, lanes] } },
      axes: [{ ...axis(t), values: xLabels }, { ...axis(t), size: width < 620 ? 44 : 78, splits: () => vm.envs.map((_, i) => i + 0.5), values: () => vm.envs.map(e => (width < 620 ? e.name.slice(0, 4) : e.name)), grid: { show: false }, ticks: { show: false } }],
      series: [{}, ...[0, 1, 2, 3, 4].map(o => ({ label: OUTCOME_LABEL[o] || 'Running', stroke: t.outcome[o], paths: () => null, points: { show: true, size: o === 0 ? 4.5 : o === 3 ? 5 : o === 2 ? 8.5 : 7.5, fill: o === 2 ? t.ground : alpha(t.outcome[o], o === 0 ? 0.5 : 0.95), stroke: o === 0 ? 'transparent' : o === 2 ? t.outcome[2] : t.ground, width: o === 0 ? 0 : o === 2 ? 2.2 : 1.2 } }))], // rolled back is a ring: failed and rolled back must not differ by hue alone
      hooks: {
        setSelect: [x => { if (x.select.width > 4) ctx.onSelect('range', { from: x.posToVal(x.select.left, 'x'), to: x.posToVal(x.select.left + x.select.width, 'x') }); x.setSelect({ left: 0, width: 0, top: 0, height: 0 }, false); }],
        setCursor: [x => { const i = x.cursor.idx, k = i == null || x.cursor.left < 0 ? -1 : vm.runs[i]; const r = x.root.getBoundingClientRect(); ctx.tip(k < 0 ? null : describe(k), r.left + x.bbox.left / devicePixelRatio + x.valToPos(ctx.ds.runs.start[k], 'x'), r.top + x.cursor.top); }],
        drawAxes: [x => { const g = x.ctx; g.save(); g.strokeStyle = t.lineStrong; g.lineWidth = devicePixelRatio; g.beginPath(); for (let i = 0; i <= lanes; i++) { const y = Math.round(x.valToPos(i, 'y', true)) + 0.5; g.moveTo(x.bbox.left, y); g.lineTo(x.bbox.left + x.bbox.width, y); } g.stroke(); g.restore(); }],
        draw: [x => { const g = x.ctx, r = devicePixelRatio; g.save(); for (const m of marks) { const x0 = x.valToPos(m.from, 'x', true), x1 = x.valToPos(m.to, 'x', true); if (x1 < x.bbox.left || x0 > x.bbox.left + x.bbox.width) continue; const l = Math.max(x0, x.bbox.left), w = Math.min(x1, x.bbox.left + x.bbox.width) - l; g.fillStyle = alpha(t.assess, 0.13); g.fillRect(l, x.bbox.top, w, x.bbox.height); g.fillStyle = t.assess; g.fillRect(l, x.bbox.top, w, 2 * r); g.scale(r, r); badge(g, t, l / r + 4, x.bbox.top / r + 6, m.n); g.setTransform(1, 0, 0, 1, 0, 0); } g.restore(); }],
      } }, data(), host);
    u.setScale('x', { min: vm.from, max: vm.to });
    const st = stack();
    b = new uPlot({ width, height: 84, legend: { show: false }, padding: [6, 8, 0, 0], cursor: { points: { show: false }, drag: { x: true, y: false, setScale: false }, y: false },
      scales: { x: { time: true }, y: { range: (_, lo, hi) => [0, Math.max(4, hi)] } },
      axes: [{ ...axis(t), values: xLabels }, { ...axis(t), size: width < 620 ? 44 : 78, splits: (_, __, lo, hi) => [0, Math.round(hi)] }],
      series: [{}, ...[3, 2, 1, 0].map(o => ({ stroke: 'transparent', fill: o === 0 ? mix(t.ground, t.outcome[0], 0.5) : t.outcome[o], paths: uPlot.paths.bars({ size: [0.82, 14], gap: 1 }), points: { show: false } }))],
      hooks: { setSelect: [x => { if (x.select.width > 4) ctx.onSelect('range', { from: x.posToVal(x.select.left, 'x'), to: x.posToVal(x.select.left + x.select.width, 'x') }); }], ready: [mark] },
    }, st, brushHost);
    b.over.addEventListener('dblclick', () => ctx.onSelect('range', null));
  }
  const xLabels = (x, splits) => { const short = splits.length > 1 && splits[1] - splits[0] < DAY; return splits.map(v => (short ? new Date(v * 1000).toISOString().slice(11, 16) : fmtDay(v))); };
  const mark = () => { if (!b) return; const l = b.valToPos(vm.from, 'x'), r = b.valToPos(vm.to, 'x'), full = vm.to - vm.from >= ctx.ds.days * DAY - 1; b.setSelect(full ? { left: 0, width: 0, top: 0, height: 0 } : { left: l, width: r - l, top: 0, height: b.bbox.height / devicePixelRatio }, false); };
  function data() { // x ascending; one column per outcome, a run sits in its environment lane, higher = longer
    const R = ctx.ds.runs, n = vm.runs.length, x = new Float64Array(n), ys = [0, 1, 2, 3, 4].map(() => new Array(n).fill(null)), lane = new Map(vm.envs.map((e, i) => [e.i, i]));
    for (let i = 0; i < n; i++) { const k = vm.runs[i]; x[i] = R.start[k]; ys[R.outcome[k]][i] = lane.get(R.e[k]) + 0.08 + LANE * Math.min(1, R.dur[k] / CAP); }
    return [x, ...ys];
  }
  function stack() { // whole period, cumulative so the bars stack: succeeded at the bottom
    // On a narrow chart a day is thinner than a pixel, so the counts are binned by week there.
    const all = ctx.brush(), c = all.counts, step = width < 620 ? 7 : 1, n = Math.ceil(all.x.length / step), x = new Float64Array(n), cum = [0, 1, 2, 3].map(() => new Float64Array(n));
    for (let i = 0; i < all.x.length; i++) { const b = Math.floor(i / step); if (i % step === 0) x[b] = all.x[i] + (step - 1) * DAY / 2; for (let o = 0; o < 4; o++) for (let q = o; q < 4; q++) cum[q][b] += c[o][i]; }
    return [x, cum[3], cum[2], cum[1], cum[0]];
  }
  const describe = k => { const R = ctx.ds.runs, p = ctx.ds.projects[R.p[k]], o = R.outcome[k]; const rows = [['When', fmtTime(R.start[k])], ['Outcome', OUTCOME_LABEL[o] || 'Running'], ['Duration', fmtDur(R.dur[k])], ['Release', 'r' + R.rel[k]]]; if (R.step[k] >= 0) rows.splice(2, 0, ['Failing step', STEPS[R.step[k]]]); if (R.t[k] >= 0) rows.push(['Tenant', ctx.ds.tenants[R.t[k]]]); return { title: `${p.name} · ${ENVS[R.e[k]]}`, rows }; };
  return {
    update(v) { const rebuild = !u || !vm || v.envs.length !== vm.envs.length; vm = v; if (rebuild) build(); else { u.setData(data(), false); u.setScale('x', { min: vm.from, max: vm.to }); b.setData(stack(), true); mark(); } },
    resize(w) { if (w === width) return; width = w; if (vm) build(); }, annotate(m) { marks = m.filter(x => x.view === 'timeline'); if (u) u.redraw(false); }, redraw() { if (vm) build(); }, dispose() { if (u) u.destroy(); if (b) b.destroy(); },
  };
}

// ---- Sparklines of the delivery figures -------------------------------------------------------
function spark(host, ctx) {
  const c = canvasFor(host, 'Trend by week'); let fig, width = 0;
  function draw() {
    if (!fig || !width) return; const t = ctx.theme, h = 36, g = c.g, s = fig.series, vals = s.filter(v => v != null); c.size(width, h); g.clearRect(0, 0, width, h); if (vals.length < 2) return;
    const lo = Math.min(...vals), hi = Math.max(...vals), X = i => 3 + i * (width - 10) / (s.length - 1), Y = v => h - 5 - (hi === lo ? 0.5 : (v - lo) / (hi - lo)) * (h - 11);
    g.beginPath(); let pen = false, last = 0; s.forEach((v, i) => { if (v == null) return; pen ? g.lineTo(X(i), Y(v)) : g.moveTo(X(i), Y(v)); pen = true; last = i; });
    g.strokeStyle = t.dim; g.lineWidth = 1.6; g.lineJoin = 'round'; g.stroke(); g.lineTo(X(last), h); g.lineTo(X(s.findIndex(v => v != null)), h); g.closePath(); g.fillStyle = alpha(t.dim, 0.1); g.fill();
    g.fillStyle = t.ground; g.beginPath(); g.arc(X(last), Y(s[last]), 5, 0, 7); g.fill(); g.fillStyle = t.info; g.beginPath(); g.arc(X(last), Y(s[last]), 3.2, 0, 7); g.fill();
  }
  return { update(f) { fig = f; draw(); }, resize(w) { width = w; draw(); }, redraw: draw };
}

export function mount(host, vm, ctx) {
  ctx = { tip() {}, split: () => false, filters: () => ({ stage: -1 }), groups: [], brush: () => vm.day, ...ctx };
  const none = { update() {}, resize() {}, annotate() {}, redraw() {}, dispose() {} }; // a host may be absent (bench: first chart alone)
  const tiles = host.overview ? tileMap(host.overview, ctx) : none, flow = host.flow ? flowView(host.flow, ctx) : none, time = host.timeline ? timeline(host.timeline, host.brush, ctx) : none, sparks = (host.figures || []).map(h => spark(h, ctx));
  const parts = [[host.overview, tiles], [host.flow, flow], [host.timeline, time], ...(host.figures || []).map((h, i) => [h, sparks[i]])].filter(p => p[0]);
  const ro = new ResizeObserver(entries => { for (const e of entries) { const p = parts.find(x => x[0] === e.target); if (p) p[1].resize(Math.floor(e.contentRect.width)); } });
  const apply = v => { vm = v; tiles.update(v); flow.update(v); time.update(v); v.figures.forEach((f, i) => sparks[i] && sparks[i].update(f)); };
  for (const [h, part] of parts) { part.resize(Math.floor(h.clientWidth)); ro.observe(h); }
  apply(vm);
  return { update: apply, annotate(m) { tiles.annotate(m); flow.annotate(m); time.annotate(m); }, retheme(t) { ctx.theme = t; tiles.redraw(); flow.redraw(); time.redraw(); sparks.forEach(s => s.redraw()); }, dispose() { ro.disconnect(); time.dispose(); for (const [h] of parts) h.replaceChildren(); if (host.brush) host.brush.replaceChildren(); } };
}
export const helpers = { fmtDay, fmtDur, mix };
