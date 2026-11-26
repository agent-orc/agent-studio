/* Canvas renderer. Immediate mode: every frame each node is laid out in screen space
   for the size it currently has, which is what makes the zoom semantic. Frames, text and
   meters keep constant stroke and type sizes; only the amount of content changes. */
(function () {
  'use strict';
  const SV = window.SV;
  const S = SV.sim;
  const R = (SV.render = {});
  const SANS = '"IBM Plex Sans", system-ui, -apple-system, "Segoe UI", Roboto, sans-serif';
  const MONO = '"IBM Plex Mono", ui-monospace, "SF Mono", Menlo, Consolas, monospace';
  const cam = (SV.cam = { x: 0, y: 0, k: 1, ox: 0, oy: 0 });
  const st = (SV.state = { layers: { perf: true, network: true, storage: false, backup: false, warnings: true }, sel: 'install', selLink: null, hover: null, hoverLink: null, reduced: false });

  const widths = new Map();
  let cv, ctx, dpr = 1, W = 0, H = 0, pal = {}, now = 0, simNow = 0, last = 0;
  const clamp = (v, a, b) => Math.max(a, Math.min(b, v));
  const X = (wx) => (wx - cam.x) * cam.k + cam.ox;
  const Y = (wy) => (wy - cam.y) * cam.k + cam.oy;
  R.X = X; R.Y = Y;
  R.wx = (sx) => (sx - cam.ox) / cam.k + cam.x;
  R.wy = (sy) => (sy - cam.oy) / cam.k + cam.y;
  const f = (weight, size, fam) => weight + ' ' + size.toFixed(2) + 'px ' + fam;

  function srect(r) {
    const x = Math.round(X(r.x)) + 0.5, y = Math.round(Y(r.y)) + 0.5;
    return { x, y, w: Math.round(X(r.x + r.w)) + 0.5 - x, h: Math.round(Y(r.y + r.h)) + 0.5 - y };
  }
  R.srect = srect;

  R.init = function (canvas) { cv = canvas; ctx = cv.getContext('2d'); R.palette(); };
  R.palette = function () {
    const cs = getComputedStyle(document.documentElement);
    ['outside', 'inside', 's1', 's2', 's3', 'ink', 'ink2', 'ink3', 'f1', 'f2', 'f3', 'grid', 'perf', 'net', 'storage', 'backup', 'warn', 'warnbg', 'onwarn', 'ok', 'track'].forEach((k) => (pal[k] = cs.getPropertyValue('--c-' + k).trim()));
    hatchCache = {}; widths.clear();
  };
  R.resize = function (w, h) {
    dpr = Math.min(window.devicePixelRatio || 1, 3); W = w; H = h;
    cv.width = Math.round(w * dpr); cv.height = Math.round(h * dpr);
    cv.style.width = w + 'px'; cv.style.height = h + 'px';
  };

  /* ---------- primitives ---------- */
  function tw(str) { const key = ctx.font + '|' + str; let w = widths.get(key); if (w === undefined) { w = ctx.measureText(str).width; if (widths.size > 4000) widths.clear(); widths.set(key, w); } return w; }
  function fit(str, max) {
    if (max <= 0) return '';
    if (tw(str) <= max) return str;
    let lo = 0, hi = str.length;
    while (lo < hi) { const mid = (lo + hi + 1) >> 1; if (tw(str.slice(0, mid) + '…') <= max) lo = mid; else hi = mid - 1; }
    return lo > 0 ? str.slice(0, lo).replace(/[\s·,]+$/, '') + '…' : '';
  }
  function text(str, x, y, font, color, align) {
    ctx.font = font; ctx.fillStyle = color; ctx.textAlign = align || 'left'; ctx.textBaseline = 'middle';
    ctx.fillText(str, x, y + 0.5);
  }
  function rrect(x, y, w, h, r) {
    r = Math.max(0, Math.min(r, w / 2, h / 2));
    ctx.beginPath();
    ctx.moveTo(x + r, y); ctx.lineTo(x + w - r, y); ctx.arcTo(x + w, y, x + w, y + r, r);
    ctx.lineTo(x + w, y + h - r); ctx.arcTo(x + w, y + h, x + w - r, y + h, r);
    ctx.lineTo(x + r, y + h); ctx.arcTo(x, y + h, x, y + h - r, r);
    ctx.lineTo(x, y + r); ctx.arcTo(x, y, x + r, y, r); ctx.closePath();
  }
  function notched(x, y, w, h, c, r) { // host service: one chamfered corner
    ctx.beginPath();
    ctx.moveTo(x + c, y); ctx.lineTo(x + w - r, y); ctx.arcTo(x + w, y, x + w, y + r, r);
    ctx.lineTo(x + w, y + h - r); ctx.arcTo(x + w, y + h, x + w - r, y + h, r);
    ctx.lineTo(x + r, y + h); ctx.arcTo(x, y + h, x, y + h - r, r);
    ctx.lineTo(x, y + c); ctx.closePath();
  }
  let hatchCache = {};
  function hatch(color, alpha) {
    const key = color + alpha;
    if (hatchCache[key]) return hatchCache[key];
    const c = document.createElement('canvas'); const s = Math.round(6 * dpr); c.width = c.height = s;
    const g = c.getContext('2d'); g.strokeStyle = color; g.globalAlpha = alpha; g.lineWidth = Math.max(1, dpr);
    g.beginPath(); g.moveTo(-1, s + 1); g.lineTo(s + 1, -1); g.moveTo(-1, 1); g.lineTo(1, -1); g.moveTo(s - 1, s + 1); g.lineTo(s + 1, s - 1); g.stroke();
    const p = ctx.createPattern(c, 'repeat');
    if (p.setTransform) p.setTransform(new DOMMatrix().scale(1 / dpr));
    return (hatchCache[key] = p);
  }
  function alpha(a, fn) { const g = ctx.globalAlpha; ctx.globalAlpha = g * a; fn(); ctx.globalAlpha = g; }
  function bar(x, y, w, h, frac, color) {
    ctx.fillStyle = pal.track; ctx.fillRect(x, y, w, h);
    ctx.fillStyle = color; ctx.fillRect(x, y, Math.max(0, w * clamp(frac, 0, 1)), h);
  }
  function spark(x, y, w, h, arr, max, color) {
    if (!arr || arr.length < 2) return;
    const n = arr.length, dx = w / (n - 2);
    const frac = S.paused ? 1 : clamp((performance.now() - S.tickAt) / 1000, 0, 1);
    ctx.save(); ctx.beginPath(); ctx.rect(x, y - 2, w, h + 4); ctx.clip();
    ctx.beginPath();
    let px = 0, py = 0;
    for (let i = 0; i < n; i++) {
      px = x + w - (n - 1 - i) * dx + (1 - frac) * dx; py = y + h - clamp(arr[i] / max, 0, 1) * h;
      if (i) ctx.lineTo(px, py); else ctx.moveTo(px, py);
    }
    ctx.strokeStyle = color; ctx.lineWidth = 1.25; ctx.lineJoin = 'round'; ctx.stroke();
    ctx.lineTo(px, y + h); ctx.lineTo(x - dx, y + h); ctx.closePath();
    alpha(0.1, () => { ctx.fillStyle = color; ctx.fill(); });
    ctx.restore();
  }
  const pct = (v) => Math.round(v * 100) + '%';
  const gb = (v) => (v >= 10 ? v.toFixed(0) : v.toFixed(1)) + ' GB';
  R.fmt = { pct, gb };

  /* ---------- eased values ---------- */
  function easeAll(dt) {
    const a = st.reduced ? 1 : 1 - Math.exp(-dt / 0.26);
    SV.order.forEach((n) => {
      const m = S.m[n.id];
      m._cpu = m._cpu == null ? m.cpu : m._cpu + (m.cpu - m._cpu) * a;
      m._memFrac = m._memFrac == null ? m.memFrac || 0 : m._memFrac + ((m.memFrac || 0) - m._memFrac) * a;
      if (m.cores) { if (!m._cores) m._cores = m.cores.slice(); for (let i = 0; i < m.cores.length; i++) m._cores[i] += (m.cores[i] - m._cores[i]) * a; }
      if (n.segs) n.segs.forEach((s) => (s._gb = s._gb == null ? s.gb : s._gb + (s.gb - s._gb) * a));
    });
  }

  /* ---------- frame ---------- */
  R.draw = function (t) {
    now = t; simNow = S.now();
    const dt = Math.min(0.1, (t - last) / 1000 || 0.016); last = t;
    easeAll(dt);
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.globalAlpha = 1;
    ctx.fillStyle = pal.outside; ctx.fillRect(0, 0, W, H);
    const b = srect(SV.nodes.install.rect);
    rrect(b.x, b.y, b.w, b.h, 14 * Math.min(1, cam.k)); ctx.fillStyle = pal.inside; ctx.fill();
    grid();
    boundary(b);
    ['net', 'cdn', 'offsite', 'app', 'ws', 'laptop'].forEach((id) => drawNode(SV.nodes[id]));
    flows(dt);
    drawLinks();
    rings();
    if (st.layers.warnings) markers();
  };

  function grid() {
    const k = cam.k;
    const minor = 20 * k, major = 100 * k;
    const x0 = R.wx(0), y0 = R.wy(0), x1 = R.wx(W), y1 = R.wy(H);
    if (minor >= 9) {
      ctx.fillStyle = pal.grid; ctx.globalAlpha = clamp((minor - 9) / 12, 0, 1) * 0.75;
      for (let gx = Math.ceil(x0 / 20) * 20; gx < x1; gx += 20) { const sx = Math.round(X(gx)); for (let gy = Math.ceil(y0 / 20) * 20; gy < y1; gy += 20) ctx.fillRect(sx, Math.round(Y(gy)), 1, 1); }
    }
    ctx.globalAlpha = clamp(major / 60, 0.35, 1); ctx.fillStyle = pal.grid;
    for (let gx = Math.ceil(x0 / 100) * 100; gx < x1; gx += 100) { const sx = Math.round(X(gx)); for (let gy = Math.ceil(y0 / 100) * 100; gy < y1; gy += 100) { const sy = Math.round(Y(gy)); ctx.fillRect(sx - 2, sy, 5, 1); ctx.fillRect(sx, sy - 2, 1, 5); } }
    ctx.globalAlpha = 1;
  }

  function boundary(b) {
    ctx.save();
    ctx.strokeStyle = pal.f1; ctx.lineWidth = 1.25; ctx.setLineDash([12, 4, 2, 4]);
    rrect(b.x, b.y, b.w, b.h, 14 * Math.min(1, cam.k)); ctx.stroke();
    ctx.restore();
    // label sits on the line, like a drawing annotation
    const fs = clamp(8.5 + cam.k * 2.2, 9, 11);
    ctx.font = f(500, fs, MONO);
    if ('letterSpacing' in ctx) ctx.letterSpacing = '0.08em';
    const full = 'SYSTEM BOUNDARY · AGENT STUDIO INSTALLATION', short = 'SYSTEM BOUNDARY';
    const label = tw(full) + 40 < b.w * 0.6 ? full : tw(short) + 40 < b.w ? short : '';
    if (label) {
      const lw = tw(label) + 14, lx = b.x + clamp(26 * cam.k, 12, 26);
      ctx.fillStyle = pal.inside; ctx.fillRect(lx, b.y - 7, lw, 14);
      text(label, lx + 7, b.y, ctx.font, pal.ink2);
    }
    if ('letterSpacing' in ctx) ctx.letterSpacing = '0px';
  }

  function visible(r) { return r.x < W + 30 && r.y < H + 30 && r.x + r.w > -30 && r.y + r.h > -30; }

  function drawNode(n) {
    if (n.layer && !st.layers[n.layer]) return;
    const r = srect(n.rect);
    if (!visible(r)) return;
    switch (n.kind) {
      case 'machine': machine(n, r); break;
      case 'vm': vm(n, r); break;
      case 'stack': stack(n, r); break;
      case 'container': case 'service': unit(n, r); break;
      case 'disk': disk(n, r); break;
      case 'external': external(n, r); break;
      case 'ghost': ghost(n, r); break;
    }
    for (let i = 0; i < n.children.length; i++) drawNode(n.children[i]);
  }

  /* ---------- group headers (machine, VM, stack) ---------- */
  function subtreeWarnings(n) { let c = 0; (function walk(x) { S.warnings.forEach((w) => { if (w.node === x.id && w.state === 'open') c++; }); x.children.forEach(walk); })(n); return c; }

  function head(n, r, strong) {
    const hp = n.headH * cam.k;
    const p = clamp(r.w * 0.035, 6, 18);
    const ts = clamp((strong ? 9.5 : 9) + r.w * 0.012, 10.5, strong ? 17 : 14.5);
    const two = hp >= ts + 22 && r.w >= 170;
    const x = r.x + p; let right = r.x + r.w - p, subRight = right;
    const cy = two ? r.y + Math.min(hp / 2 - 7, p + ts / 2 + 6) : r.y + Math.max(hp / 2, ts / 2 + 4);
    if (hp < 13 || r.w < 40) return;
    const m = S.m[n.id], L = st.layers;

    // right side: live summary (PERF)
    if (L.perf && (n.perfHost || n.hostCpu) && r.w >= 150) {
      const cpuTxt = 'cpu ' + pct(m._cpu);
      if (n.perfHost && r.w >= 330 && hp >= 34) {
        const cs = clamp(hp * 0.2, 6, 10), gap = 2, cw = n.cores * (cs + gap) - gap;
        const cx = right - cw, cyy = cy - cs / 2;
        for (let i = 0; i < n.cores; i++) {
          ctx.fillStyle = pal.track; ctx.fillRect(cx + i * (cs + gap), cyy, cs, cs);
          ctx.globalAlpha = clamp(0.1 + m._cores[i] * 1.05, 0, 1); ctx.fillStyle = pal.perf; ctx.fillRect(cx + i * (cs + gap), cyy, cs, cs); ctx.globalAlpha = 1;
        }
        text(cpuTxt, cx - 8, cyy + cs / 2, f(500, 10.5, MONO), pal.perf, 'right');
        const memRow = two && r.w >= 470;
        if (memRow) {
          bar(cx, cyy + cs + 6, cw, 3, m._memFrac, pal.perf);
          text('mem ' + m.mem.toFixed(0) + '/' + n.memMax + ' GB', cx - 8, cyy + cs + 7.5, f(400, 10.5, MONO), pal.ink2, 'right');
        }
        right = cx - 8 - 62; if (memRow) subRight = cx - 8 - 96;
      } else {
        ctx.font = f(500, 10.5, MONO);
        text(cpuTxt, right, cy, ctx.font, pal.perf, 'right');
        right -= tw(cpuTxt) + 10;
      }
    }
    // title, then roll-up of warnings in the subtree
    ctx.font = f(600, ts, SANS);
    const wc = L.warnings && strong ? subtreeWarnings(n) : 0;
    const badgeW = wc ? 30 : 0;
    const name = fit(n.name, right - x - badgeW);
    text(name, x, cy, ctx.font, pal.ink);
    if (wc && name) {
      const bx = x + tw(name) + 9;
      tri(bx + 5, cy, 5.5, pal.warn);
      text(String(wc), bx + 14, cy, f(500, 11, MONO), pal.warn);
    }
    if (two) text(fit0(n.sub, f(400, clamp(ts - 3, 10, 11.5), MONO), subRight - x), x, cy + ts / 2 + 9, f(400, clamp(ts - 3, 10, 11.5), MONO), pal.ink2);
  }
  function fit0(str, font, max) { ctx.font = font; return fit(str, max); }
  function tri(cx, cy, s, color) { ctx.beginPath(); ctx.moveTo(cx, cy - s); ctx.lineTo(cx + s * 1.05, cy + s * 0.85); ctx.lineTo(cx - s * 1.05, cy + s * 0.85); ctx.closePath(); ctx.fillStyle = color; ctx.fill(); }

  function machine(n, r) {
    const rad = clamp(12 * cam.k, 4, 12);
    rrect(r.x, r.y, r.w, r.h, rad); ctx.fillStyle = pal.s1; ctx.fill();
    ctx.strokeStyle = pal.f1; ctx.lineWidth = 1.5; ctx.stroke();
    head(n, r, true);
    if (n.light) laptopBody(n, r);
    if (n.id === 'app' && cam.k > 0.5) {
      // annotation between the stack and the host services
      const y = Y(n.rect.y + 336), x = X(n.rect.x + 24);
      alpha(clamp((cam.k - 0.5) * 4, 0, 1), () => caps('HOST SERVICES', x, y, pal.ink3));
    }
  }
  function caps(str, x, y, color, align) {
    ctx.font = f(500, 9.5, MONO);
    if ('letterSpacing' in ctx) ctx.letterSpacing = '0.08em';
    text(str, x, y, ctx.font, color, align);
    if ('letterSpacing' in ctx) ctx.letterSpacing = '0px';
  }
  function laptopBody(n, r) {
    const hp = n.headH * cam.k; const p = clamp(r.w * 0.035, 6, 18);
    if (r.h - hp < 16 || r.w < 150) return;
    const y = r.y + hp + (r.h - hp) / 2 - 3;
    const L = st.layers;
    ctx.font = f(400, 10.5, MONO);
    const msg = pick(L.network ? ['overlay peer · latency ' + Math.round(S.latency) + ' ms', 'latency ' + Math.round(S.latency) + ' ms'] : ['no agent here · link state only', 'link state only'], r.w - 2 * p);
    text(msg, r.x + p, y, f(400, 10.5, MONO), L.network ? pal.net : pal.ink2);
  }
  function vm(n, r) {
    const rad = clamp(9 * cam.k, 3, 9);
    rrect(r.x, r.y, r.w, r.h, rad); ctx.fillStyle = pal.s2; ctx.fill();
    ctx.strokeStyle = pal.f2; ctx.lineWidth = 1; ctx.stroke();
    if (r.w > 60) { rrect(r.x + 3, r.y + 3, r.w - 6, r.h - 6, Math.max(1, rad - 2)); ctx.stroke(); }
    head({ ...n, headH: n.headH }, { x: r.x + 3, y: r.y + 3, w: r.w - 6, h: r.h - 6 }, false);
  }
  function stack(n, r) {
    ctx.save(); ctx.setLineDash([5, 4]); ctx.strokeStyle = pal.f2; ctx.lineWidth = 1;
    rrect(r.x, r.y, r.w, r.h, clamp(8 * cam.k, 3, 8)); ctx.stroke(); ctx.restore();
    head(n, r, false);
  }

  /* ---------- containers and host services ---------- */
  function unit(n, r) {
    const m = S.m[n.id], L = st.layers;
    const rad = clamp(7 * cam.k, 2, 7);
    if (n.kind === 'service') notched(r.x, r.y, r.w, r.h, clamp(11 * cam.k, 4, 12), Math.min(rad, 3)); else rrect(r.x, r.y, r.w, r.h, rad);
    ctx.fillStyle = pal.s3; ctx.fill(); ctx.strokeStyle = pal.f3; ctx.lineWidth = 1; ctx.stroke();
    const warn = L.warnings ? S.warningsFor(n.id).filter((w) => w.state === 'open') : [];
    const usedN = n.slots ? S.used(n.id) : 0;

    if (r.w < 44 || r.h < 22) { // too small for text: a level gauge
      if (L.perf && n.slots) { const fr = usedN / n.slots; alpha(0.5, () => { ctx.fillStyle = pal.perf; ctx.fillRect(r.x + 1, r.y + r.h - 1 - (r.h - 2) * fr, r.w - 2, (r.h - 2) * fr); }); }
      return;
    }
    const p = clamp(r.w * 0.06, 6, 18);
    const x = r.x + p + (n.kind === 'service' ? clamp(r.w * 0.02, 0, 4) : 0), w = r.x + r.w - p - x;
    let y = r.y + clamp(p * 0.8, 5, 14);
    const bottom = r.y + r.h - clamp(p * 0.6, 4, 12);
    const detail = w >= 270 && r.h >= 150;
    const labelled = w >= 96;
    const u = detail ? clamp(w / 430, 1, 1.4) : 1;
    const ts = detail ? 16.5 + (u - 1) * 6 : clamp(9.5 + r.w * 0.022, 10.5, 16.5);
    let stop = false;
    function row(h, gap, fn) {
      if (stop) return; if (y + h > bottom + 0.5) { stop = true; return; }
      const fade = clamp((bottom - (y + h)) / 5 + 0.55, 0, 1);
      alpha(fade, () => fn(y, h)); y += h + gap;
    }

    row(ts + 3, 2, (yy, h) => text(fit0(n.name, f(600, ts, SANS), w - (warn.length ? 8 : 0)), x, yy + h / 2, f(600, ts, SANS), pal.ink));
    if (r.w >= 190) row(13, detail ? 8 : 4, (yy, h) => text(fit0(n.sub, f(400, detail ? 11.5 : 10.5, MONO), w), x, yy + h / 2, ctx.font, pal.ink2));
    else y += 2;

    if (warn.length && r.w >= 118) {
      row(18, 5, (yy, h) => {
        rrect(x - 3, yy, w + 6, h, 3); ctx.fillStyle = pal.warnbg; ctx.fill();
        ctx.font = f(500, 10.5, MONO); const full = warn[0].short + (n.id === 'r3' && detail ? ': slots held back' : '');
        text(tw(full) <= w - 4 ? full : tw(warn[0].short) <= w - 4 ? warn[0].short : fit(warn[0].tiny, w - 4), x + 2, yy + h / 2, ctx.font, pal.warn);
      });
      if (detail && r.h >= 260) row(14, 6, (yy, h) => text(fit0('next: ' + warn[0].action, f(400, 10.5, MONO), w), x, yy + h / 2, ctx.font, pal.ink2));
    }

    if (L.perf) {
      if (n.slots) {
        if (detail) tiles(n, m, x, w, row, u);
        else if (r.w >= 104) row(clamp(w / n.slots - 2, 6, 12), 5, (yy, h) => {
          const s = h, gap = clamp(s * 0.28, 2, 3);
          const cw = cells(n, m, x, yy, s, gap);
          const lab = usedN + '/' + n.slots + (labelled ? ' slots' : '');
          ctx.font = f(500, 10.5, MONO);
          if (cw + 8 + tw(lab) <= w) text(lab, x + w, yy + h / 2, ctx.font, pal.ink, 'right');
        });
        else row(12, 3, (yy, h) => text(usedN + '/' + n.slots, x, yy + h / 2, f(500, 11, MONO), pal.perf));
      }
      if (n.gate) gateRow(n, m, x, w, row, detail, labelled);
      if (n.queue && r.w >= 104 && !detail) queueRow(n, x, w, row, detail, labelled);
      if (n.rate && r.w >= 104) {
        const v = S.link[n.rate === 'api' ? 'webapi' : 'origin'].rate;
        if (!detail) row(13, 3, (yy, h) => { text(labelled ? 'requests' : 'req', x, yy + h / 2, f(400, 10, MONO), pal.ink2); text(v.toFixed(0) + '/s', x + w, yy + h / 2, f(500, 10.5, MONO), pal.ink, 'right'); });
      }
      if (n.queues && labelled) row(13, 4, (yy, h) => {
        const q = S.queues;
        ctx.font = f(400, 10.5, MONO); const full = 'queued  coding ' + q.coding + ' · review ' + q.review + ' · gate ' + q.gate;
        if (tw(full) <= w) text(full, x, yy + h / 2, ctx.font, pal.ink2);
        else { text('queued', x, yy + h / 2, f(400, 10, MONO), pal.ink2); text(String(q.coding + q.review + q.gate), x + w, yy + h / 2, f(500, 10.5, MONO), pal.ink, 'right'); }
      });
      if (detail) {
        const hmax = (k, floor) => Math.max(floor, Math.max.apply(null, S.hist[k])) * 1.2;
        const boxes = [];
        if (n.queue) boxes.push(['QUEUE', S.queues[n.queue] + ' waiting', 'q.' + n.queue, 3]);
        if (n.rate) { const lk = 'l.' + (n.rate === 'api' ? 'webapi' : 'origin'); boxes.push(['REQUESTS', S.link[lk.slice(2)].rate.toFixed(0) + ' req/s', lk, 5]); }
        boxes.push(['CPU', pct(m._cpu) + ' of host', n.id + '.cpu', 0.05], ['MEMORY', gb(m.mem), n.id + '.mem', 0.5]);
        const gap = 14, bw = (w - gap * (boxes.length - 1)) / boxes.length, narrow = bw < 150;
        row(Math.round((narrow ? 60 : 42) * u), 10, (yy, h) => boxes.forEach((b, i) => sparkBox(b[0], b[1], S.hist[b[2]], hmax(b[2], b[3]), x + i * (bw + gap), yy, bw, h, pal.perf, narrow)));
      } else if (r.w >= 104) {
        if (labelled) { meter('cpu', m._cpu * 3.2, pct(m._cpu), x, w, row); meter('mem', m._memFrac * 4, gb(m.mem), x, w, row); }
      }
    }
    if (r.w >= 112) {
      const note = (cands, color) => row(14, 3, (yy, h) => { ctx.font = f(400, 10.5, MONO); dot(x + 3, yy + h / 2, color); text(pick(cands, w - 12), x + 12, yy + h / 2, ctx.font, color); });
      if (L.network && n.netNote) note([n.netNote, n.netShort], pal.net);
      if (L.storage && n.volumes) { const v = n.volumes.map(volName); note(['mounts ' + v.join(' + '), v.join(' + '), v[0] + (v.length > 1 ? ' +' + (v.length - 1) : '')], pal.storage); }
      if (L.backup && (n.backupNote || n.volumes)) note(n.backupNote ? [n.backupNote, 'backup not configured', 'not configured'] : n.parent === 'vm' ? ['not backed up: rebuilt per review', 'rebuilt per review', 'no backup needed'] : ['data has no off-host copy', 'no off-host copy'], pal.backup);
    }
    if (detail && n.facts) n.facts.slice(0, 3).forEach((kv) => row(14, 2, (yy, h) => { text(kv[0].toLowerCase(), x, yy + h / 2, f(400, 10.5, MONO), pal.ink3); text(fit0(kv[1], f(400, 10.5, MONO), w - 104), x + 104, yy + h / 2, ctx.font, pal.ink2); }));
  }
  function pick(cands, max) { for (let i = 0; i < cands.length; i++) if (cands[i] && tw(cands[i]) <= max) return cands[i]; return fit(cands[cands.length - 1], max); }
  function volName(id) { let out = id; ['appdisk', 'vmdisk'].forEach((d) => SV.nodes[d].segs.forEach((s) => { if (s.id === id) out = s.short; })); return out; }
  function dot(x, y, color) { ctx.beginPath(); ctx.arc(x, y, 2.5, 0, 6.2832); ctx.fillStyle = color; ctx.fill(); }

  function slotState(n, m, i) { return i >= n.slots - m.held ? 'held' : m.slots[i] ? 'busy' : 'free'; }
  function cells(n, m, x, y, s, gap) {
    for (let i = 0; i < n.slots; i++) {
      const cx = x + i * (s + gap), state = slotState(n, m, i), age = now - m.slotCh[i];
      if (state === 'held') { ctx.fillStyle = hatch(pal.warn, 1); ctx.fillRect(cx, y, s, s); ctx.strokeStyle = pal.warn; ctx.lineWidth = 1; ctx.strokeRect(cx + 0.5, y + 0.5, s - 1, s - 1); continue; }
      ctx.fillStyle = pal.track; ctx.fillRect(cx, y, s, s);
      if (state === 'busy') {
        const g = st.reduced ? 1 : clamp(age / 240, 0.2, 1), o = (s * (1 - g)) / 2;
        ctx.fillStyle = pal.perf; ctx.fillRect(cx + o, y + o, s * g, s * g);
      } else if (age < 700 && !st.reduced) alpha((1 - age / 700) * 0.7, () => { ctx.fillStyle = pal.perf; ctx.fillRect(cx, y, s, s); });
      if (age < 1100 && !st.reduced) { const e = age / 1100; alpha((1 - e) * 0.9, () => { ctx.strokeStyle = pal.perf; ctx.lineWidth = 1.25; ctx.strokeRect(cx - 1 - e * 4, y - 1 - e * 4, s + 2 + e * 8, s + 2 + e * 8); }); }
    }
    return n.slots * (s + gap) - gap;
  }
  function tiles(n, m, x, w, row, u) {
    const cols = n.slots <= 5 ? n.slots : 5, gap = 6, tw_ = (w - (cols - 1) * gap) / cols, th = Math.round(34 * u);
    const rows = Math.ceil(n.slots / cols);
    for (let rI = 0; rI < rows; rI++) row(th, rI === rows - 1 ? 8 : gap, (yy) => {
      for (let c = 0; c < cols; c++) {
        const i = rI * cols + c; if (i >= n.slots) break;
        const tx = Math.round(x + c * (tw_ + gap)), state = slotState(n, m, i), age = now - m.slotCh[i], job = m.slots[i];
        const bw = Math.round(tw_);
        if (state === 'held') {
          ctx.fillStyle = hatch(pal.warn, 0.8); ctx.fillRect(tx, yy, bw, th); ctx.strokeStyle = pal.warn; ctx.lineWidth = 1; ctx.strokeRect(tx + 0.5, yy + 0.5, bw - 1, th - 1);
          ctx.font = f(500, 10.5, MONO); const lab = fit('held back', bw - 12), lw = tw(lab);
          if (lab) { ctx.fillStyle = pal.s3; ctx.fillRect(tx + 5, yy + th * 0.3 - 7, lw + 6, 14); text(lab, tx + 8, yy + th * 0.3, ctx.font, pal.warn); }
        } else if (state === 'busy') {
          ctx.fillStyle = pal.track; ctx.fillRect(tx, yy, bw, th);
          const pr = clamp((simNow - job.start) / job.dur, 0, 1);
          alpha(0.16, () => { ctx.fillStyle = pal.perf; ctx.fillRect(tx, yy, bw * pr, th); });
          ctx.fillStyle = pal.perf; ctx.fillRect(tx, yy + th - 3, bw * pr, 3);
          ctx.font = f(500, 10.5, MONO);
          text(fit(bw >= 74 ? 'task ' + job.id : String(job.id), bw - 10), tx + 6, yy + th * 0.3, ctx.font, pal.ink);
          const el = Math.max(0, Math.floor(simNow - job.start));
          text(fit(Math.floor(el / 60) + ':' + String(el % 60).padStart(2, '0'), bw - 10), tx + 6, yy + th * 0.64, f(400, 10, MONO), pal.ink2);
        } else {
          ctx.save(); ctx.setLineDash([3, 3]); ctx.strokeStyle = pal.f3; ctx.lineWidth = 1; ctx.strokeRect(tx + 0.5, yy + 0.5, bw - 1, th - 1); ctx.restore();
          text('free', tx + 6, yy + th * 0.3, f(400, 10.5, MONO), pal.ink3);
        }
        if (age < 1100 && !st.reduced) { const e = age / 1100; alpha((1 - e) * 0.9, () => { ctx.strokeStyle = pal.perf; ctx.lineWidth = 1.5; ctx.strokeRect(tx - e * 4, yy - e * 4, bw + e * 8, th + e * 8); }); }
      }
    });
  }
  function gateRow(n, m, x, w, row, detail, labelled) {
    const job = m.job;
    if (!labelled) { row(12, 3, (yy, h) => text(job ? 'batch ' + job.batch : 'idle', x, yy + h / 2, f(500, 10.5, MONO), job ? pal.perf : pal.ink3)); return; }
    row(detail ? 30 : 22, 5, (yy, h) => {
      if (job) {
        const su = S.suiteOf(job, simNow);
        text(fit0('batch of ' + job.batch + ' · suite ' + su.index + '/' + su.total + ' ' + su.name, f(500, 10.5, MONO), w), x, yy + 6, ctx.font, pal.ink);
        const bh = detail ? 6 : 4, by = yy + h - bh - 1;
        ctx.fillStyle = pal.track; ctx.fillRect(x, by, w, bh);
        ctx.fillStyle = pal.perf; ctx.fillRect(x, by, w * su.p, bh);
        ctx.fillStyle = pal.s3; for (let i = 1; i < su.total; i++) ctx.fillRect(Math.round(x + (w * i) / su.total), by, 1, bh);
      } else {
        text(fit0('idle · waiting for a batch', f(400, 10.5, MONO), w), x, yy + 6, ctx.font, pal.ink3);
        ctx.fillStyle = pal.track; ctx.fillRect(x, yy + h - (detail ? 7 : 5), w, detail ? 6 : 4);
      }
      const age = now - (m.ch || -1e9);
      if (age < 1100 && !st.reduced) { const e = age / 1100; alpha((1 - e) * 0.9, () => { ctx.strokeStyle = pal.perf; ctx.lineWidth = 1.25; ctx.strokeRect(x - 2 - e * 3, yy + h - 8 - e * 3, w + 4 + e * 6, 9 + e * 6); }); }
    });
  }
  function queueRow(n, x, w, row, detail, labelled) {
    const q = S.queues[n.queue];
    row(13, detail ? 8 : 4, (yy, h) => {
      const lab = !labelled ? 'q' : n.queue === 'review' && w >= 190 ? 'review queue' : 'queue';
      text(lab, x, yy + h / 2, f(400, 10, MONO), pal.ink2);
      const lx = x + tw(lab) + 8, max = Math.max(0, Math.floor((w - (lx - x) - 22) / 7));
      const shown = Math.min(q, max);
      ctx.fillStyle = pal.ink;
      for (let i = 0; i < shown; i++) { ctx.beginPath(); ctx.arc(lx + i * 7 + 2.5, yy + h / 2, 2.25, 0, 6.2832); ctx.fill(); }
      text(String(q), x + w, yy + h / 2, f(500, 10.5, MONO), q ? pal.ink : pal.ink3, 'right');
    });
  }
  function meter(label, frac, value, x, w, row) {
    row(13, 2, (yy, h) => {
      const lw = 28, vw = 46;
      text(label, x, yy + h / 2, f(400, 10, MONO), pal.ink2);
      bar(x + lw, yy + h / 2 - 1.5, w - lw - vw, 3, frac, pal.perf);
      text(value, x + w, yy + h / 2, f(400, 10.5, MONO), pal.ink, 'right');
    });
  }
  function sparkBox(label, value, arr, max, x, y, w, h, color, narrow) {
    ctx.fillStyle = pal.f3; ctx.fillRect(x, y, w, 1);
    caps(label, x, y + 11, pal.ink2);
    text(fit0(value, f(500, 13, MONO), w), x, y + 29, ctx.font, pal.ink);
    if (narrow) { spark(x, y + 40, w, h - 42, arr, max || 1, color); return; }
    const sx = x + Math.max(tw(value) + 12, w * 0.46);
    if (x + w - sx > 24) spark(sx, y + 8, x + w - sx, h - 12, arr, max || 1, color);
  }

  /* ---------- storage strips ---------- */
  function disk(n, r) {
    const m = S.m[n.id], L = st.layers;
    rrect(r.x, r.y, r.w, r.h, Math.min(r.h / 2, clamp(10 * cam.k, 3, 10)));
    ctx.fillStyle = pal.s3; ctx.fill(); ctx.strokeStyle = pal.f3; ctx.lineWidth = 1; ctx.stroke();
    if (r.w < 40) return;
    const p = clamp(r.h * 0.14, 4, 14), px = clamp(r.w * 0.03, 6, 16);
    const x = r.x + px, w = r.w - 2 * px;
    let y = r.y + p; const bottom = r.y + r.h - p;
    const over = m.frac > n.threshold && L.warnings;
    const segOn = L.storage || L.backup;
    const selUnit = st.sel && SV.nodes[st.sel] && SV.nodes[st.sel].volumes;
    if (r.h >= 32 && r.w >= 120) {
      const ts = clamp(9.5 + r.h * 0.05, 10.5, 13.5);
      ctx.font = f(600, ts, SANS); const nm = n.name; text(nm, x, y + 7, ctx.font, pal.ink);
      const nx = x + tw(nm) + 10;
      const val = pct(m.frac) + (r.w >= 300 ? ' · ' + m.used.toFixed(0) + ' of ' + n.capacity + ' GB' : '');
      ctx.font = f(500, 10.5, MONO); const vw = tw(val);
      text(val, x + w, y + 7, ctx.font, over ? pal.warn : pal.ink, 'right');
      if (x + w - vw - 12 - nx > 60) text(fit0(n.sub, f(400, 10.5, MONO), x + w - vw - 12 - nx), nx, y + 7.5, ctx.font, pal.ink2);
      y += 19;
    }
    const labels = segOn && bottom - y >= 30 && r.w >= 260;
    const avail = bottom - y - (labels ? 15 : 0);
    const two = n.mirror && L.storage && avail >= 20;
    const bh = clamp(two ? (avail - 3) / 2 : avail, 3, 13);
    const bars = two ? [y, y + bh + 3] : [y + Math.max(0, (avail - bh) / 2)];
    bars.forEach((by, bi) => {
      ctx.fillStyle = pal.track; ctx.fillRect(x, by, w, bh);
      let sx = x;
      n.segs.forEach((s) => {
        const sw = (s._gb / n.capacity) * w;
        if (!segOn) { ctx.fillStyle = pal.ink2; alpha(0.55, () => ctx.fillRect(sx, by, sw, bh)); }
        else {
          const dim = selUnit && s.data && SV.nodes[st.sel].volumes.indexOf(s.id) < 0 ? 0.4 : 1;
          if (s.snapshot) { alpha(0.28, () => { ctx.fillStyle = L.backup ? pal.backup : pal.storage; ctx.fillRect(sx, by, sw, bh); }); if (L.backup) { ctx.fillStyle = hatch(pal.backup, 1); ctx.fillRect(sx, by, sw, bh); } }
          else if (s.data) { alpha((L.storage ? 0.85 : 0.3) * dim, () => { ctx.fillStyle = L.storage ? pal.storage : pal.ink2; ctx.fillRect(sx, by, sw, bh); }); }
          else alpha(0.4, () => { ctx.fillStyle = pal.ink2; ctx.fillRect(sx, by, sw, bh); });
          ctx.fillStyle = pal.s3; ctx.fillRect(Math.round(sx + sw) - 1, by, 1, bh);
          if (L.backup && s.data && bi === 0 && !s.ephemeral) { ctx.save(); ctx.setLineDash([3, 2]); ctx.strokeStyle = pal.backup; ctx.lineWidth = 1.25; ctx.strokeRect(Math.round(sx) + 1.5, bars[0] - 1.5, sw - 4, (two ? bh * 2 + 3 : bh) + 3); ctx.restore(); }
        }
        sx += sw;
      });
      if (over && !segOn) { const tx = x + w * n.threshold; ctx.fillStyle = pal.warn; ctx.fillRect(tx, by, sx - tx, bh); }
    });
    // alert threshold tick
    const tx = Math.round(x + w * n.threshold) + 0.5, top = bars[0], bot = bars[bars.length - 1] + bh;
    ctx.strokeStyle = over ? pal.warn : pal.ink2; ctx.lineWidth = 1; ctx.beginPath(); ctx.moveTo(tx, top - 3); ctx.lineTo(tx, bot + 3); ctx.stroke();
    if (labels) {
      let sx = x; const ly = bot + 9;
      n.segs.forEach((s) => {
        const sw = (s._gb / n.capacity) * w;
        const col = s.snapshot && L.backup ? pal.backup : s.data && L.storage ? pal.storage : pal.ink2;
        ctx.font = f(400, 10, MONO); const g = s._gb.toFixed(0) + ' GB';
        const cands = s.snapshot ? (L.backup ? ['snapshot, same disk ' + g, 'snapshot ' + g, 'snapshot', 'snap'] : ['manual snapshot ' + g, 'snapshot ' + g, 'snapshot', 'snap']) : [s.name + ' ' + g, s.short + ' ' + g, s.short, s.short.split(' ')[0]];
        const lab = cands.find((c) => tw(c) <= sw - 6);
        if (lab) text(lab, sx + 1, ly, ctx.font, col);
        sx += sw;
      });
      const free = n.capacity - m.used, fw = x + w - sx;
      ctx.font = f(400, 10, MONO); const fl = ['free ' + free.toFixed(0) + ' GB', 'free ' + free.toFixed(0), 'free'].find((c) => tw(c) <= fw - 4);
      if (fl) text(fl, sx + 3, ly, ctx.font, over ? pal.warn : pal.ink2);
    }
  }

  function external(n, r) {
    ctx.save(); ctx.setLineDash([2, 3]); ctx.strokeStyle = pal.f2; ctx.lineWidth = 1.25;
    rrect(r.x, r.y, r.w, r.h, clamp(8 * cam.k, 3, 8)); ctx.stroke(); ctx.restore();
    if (r.w < 44) return;
    const p = clamp(r.w * 0.07, 5, 14), w = r.w - 2 * p;
    const ts = clamp(9.5 + r.w * 0.02, 10.5, 15);
    const L = st.layers;
    let sub = n.sub;
    if (L.network) { sub = n.id === 'net' ? S.link.pub.rate.toFixed(0) + ' req/s' : 'cache hit ' + pct(S.cache) + ' · ' + S.link.origin.rate.toFixed(0) + ' req/s to origin'; ctx.font = f(400, 10.5, MONO); if (tw(sub) > w && n.id === 'cdn') sub = 'cache hit ' + pct(S.cache); }
    const two = r.h >= ts + 24;
    text(fit0(n.name, f(600, ts, SANS), w), r.x + p, r.y + r.h / 2 - (two ? 8 : 0), ctx.font, pal.ink);
    if (two) text(fit0(sub, f(400, 10.5, MONO), w), r.x + p, r.y + r.h / 2 + ts / 2 + 3, ctx.font, L.network ? pal.net : pal.ink2);
  }
  function ghost(n, r) {
    rrect(r.x, r.y, r.w, r.h, clamp(8 * cam.k, 3, 8));
    ctx.fillStyle = hatch(pal.backup, 0.35); ctx.fill();
    ctx.save(); ctx.setLineDash([5, 4]); ctx.strokeStyle = pal.backup; ctx.lineWidth = 1.25; ctx.stroke(); ctx.restore();
    if (r.w < 60) return;
    const p = clamp(r.w * 0.07, 5, 14), w = r.w - 2 * p;
    const ts = clamp(9 + r.w * 0.02, 10.5, 14);
    const two = r.h >= ts + 24;
    ctx.font = f(600, ts, SANS); const name = fit(r.w >= 150 ? n.name : 'Off-host target', w);
    ctx.fillStyle = pal.outside; ctx.fillRect(r.x + p - 3, r.y + r.h / 2 - (two ? 8 : 0) - ts / 2 - 2, tw(name) + 6, ts + 4);
    text(name, r.x + p, r.y + r.h / 2 - (two ? 8 : 0), ctx.font, pal.ink);
    if (two) { ctx.font = f(500, 10.5, MONO); const s = fit('not configured', w); ctx.fillStyle = pal.outside; ctx.fillRect(r.x + p - 3, r.y + r.h / 2 + ts / 2 - 4, tw(s) + 6, 15); text(s, r.x + p, r.y + r.h / 2 + ts / 2 + 3, ctx.font, pal.backup); }
  }

  /* ---------- links ---------- */
  const VIS = { pub: 0.11, origin: 0.11, webapi: 0.11, overlay: 0.014, tunnel: 0.3 };
  const SPEED = 78; // world units per second
  const parts = {};
  function flows(dt) {
    if (st.reduced || S.paused) return;
    SV.links.forEach((l) => {
      const v = VIS[l.id]; if (!v) return;
      const ps = parts[l.id] || (parts[l.id] = { acc: Math.random(), list: [], seeded: false });
      const rate = Math.min(5, S.link[l.id].rate * v);
      if (!ps.seeded) { ps.seeded = true; for (let d = Math.random() * 30; d < l.len; d += SPEED / Math.max(0.3, rate)) ps.list.push({ d }); }
      ps.acc += rate * dt;
      while (ps.acc >= 1) { ps.acc -= 1; ps.list.push({ d: 0 }); }
      for (let i = ps.list.length - 1; i >= 0; i--) { ps.list[i].d += SPEED * dt; if (ps.list[i].d > l.len) ps.list.splice(i, 1); }
    });
  }
  function at(l, d) {
    d = clamp(d, 0, l.len);
    for (let i = 1; i < l.pts.length; i++) if (d <= l.cum[i] || i === l.pts.length - 1) {
      const t = (d - l.cum[i - 1]) / Math.max(1e-6, l.cum[i] - l.cum[i - 1]);
      return [X(l.pts[i - 1][0] + (l.pts[i][0] - l.pts[i - 1][0]) * t), Y(l.pts[i - 1][1] + (l.pts[i][1] - l.pts[i - 1][1]) * t)];
    }
    return [X(l.pts[0][0]), Y(l.pts[0][1])];
  }
  function trace(pts, off) {
    ctx.beginPath();
    for (let i = 0; i < pts.length; i++) {
      let px = pts[i][0], py = pts[i][1];
      if (off) {
        const a = pts[Math.max(0, i - 1)], b = pts[Math.min(pts.length - 1, i + 1)];
        const d1 = i > 0 ? unitv(a, pts[i]) : null, d2 = i < pts.length - 1 ? unitv(pts[i], b) : null;
        const n1 = d1 ? [-d1[1], d1[0]] : null, n2 = d2 ? [-d2[1], d2[0]] : null;
        const nx = (n1 ? n1[0] : 0) + (n2 ? n2[0] : 0), ny = (n1 ? n1[1] : 0) + (n2 ? n2[1] : 0);
        const straight = n1 && n2 && Math.abs(n1[0] - n2[0]) < 0.01 && Math.abs(n1[1] - n2[1]) < 0.01;
        const sc = n1 && n2 && straight ? 0.5 : 1;
        px += nx * off * sc; py += ny * off * sc;
      }
      if (i) ctx.lineTo(px, py); else ctx.moveTo(px, py);
    }
  }
  function unitv(a, b) { const dx = b[0] - a[0], dy = b[1] - a[1], l = Math.hypot(dx, dy) || 1; return [dx / l, dy / l]; }

  function drawLinks() {
    const L = st.layers;
    SV.links.forEach((l) => {
      if (l.layer && !L[l.layer]) return;
      const pts = l.pts.map((p) => [Math.round(X(p[0])) + 0.5, Math.round(Y(p[1])) + 0.5]);
      const on = l.kind === 'backup' ? true : L.network;
      const color = l.kind === 'backup' ? pal.backup : on ? pal.net : pal.ink3;
      const sel = st.selLink === l.id, hov = st.hoverLink === l.id;
      ctx.save(); ctx.lineJoin = 'miter'; ctx.lineCap = 'butt';
      if (sel || hov) { trace(pts); ctx.strokeStyle = color; ctx.globalAlpha = sel ? 0.28 : 0.16; ctx.lineWidth = 9; ctx.stroke(); ctx.globalAlpha = 1; }
      if (!on) { trace(pts); ctx.strokeStyle = color; ctx.lineWidth = 1; ctx.stroke(); ctx.restore(); return; }
      ctx.strokeStyle = color;
      if (l.kind === 'tunnel') {
        trace(pts); ctx.globalAlpha = 0.13; ctx.lineWidth = 7; ctx.stroke(); ctx.globalAlpha = 1;
        ctx.lineWidth = 1.1; trace(pts, 3.5); ctx.stroke(); trace(pts, -3.5); ctx.stroke();
      } else if (l.kind === 'overlay') { ctx.setLineDash([7, 4]); ctx.lineWidth = 1.5; trace(pts); ctx.stroke(); }
      else if (l.kind === 'backup') { ctx.setLineDash([2, 5]); ctx.lineWidth = 1.5; trace(pts); ctx.stroke(); }
      else { ctx.lineWidth = l.kind === 'public' ? 1.6 : 1.2; trace(pts); ctx.stroke(); }
      ctx.restore();

      // frame crossings: a small port on every frame the link passes through
      l.cross.forEach((c) => {
        const cx = Math.round(X(c.x)) + 0.5, cy = Math.round(Y(c.y)) + 0.5, big = c.node.kind === 'boundary';
        const s = big ? 4.5 : 3;
        if (c.node.kind === 'stack' || cam.k * (big ? 3 : 1) < 0.42) return;
        ctx.fillStyle = big ? color : pal.s3; ctx.strokeStyle = color; ctx.lineWidth = 1.25;
        ctx.beginPath(); ctx.rect(cx - s, cy - s, s * 2, s * 2); ctx.fill(); ctx.stroke();
      });
      // direction
      if (l.arrow) {
        const a = pts[pts.length - 2], b = pts[pts.length - 1], d = unitv(a, b), s = 5.5;
        const bx = b[0] - d[0] * 1, by = b[1] - d[1] * 1;
        ctx.beginPath(); ctx.moveTo(bx, by); ctx.lineTo(bx - d[0] * s * 1.7 - d[1] * s, by - d[1] * s * 1.7 + d[0] * s); ctx.lineTo(bx - d[0] * s * 1.7 + d[1] * s, by - d[1] * s * 1.7 - d[0] * s); ctx.closePath();
        ctx.fillStyle = color; ctx.fill();
      } else if (l.kind === 'overlay') { [pts[0], pts[pts.length - 1]].forEach((p) => { ctx.beginPath(); ctx.arc(p[0], p[1], 3, 0, 6.2832); ctx.fillStyle = pal.s3; ctx.fill(); ctx.strokeStyle = color; ctx.lineWidth = 1.25; ctx.stroke(); }); }
      if (l.broken) brokenMark(l, color);

      // flow: one dot per unit of traffic, so density is the rate
      const ps = parts[l.id];
      if (ps && L.network && !st.reduced) { ctx.fillStyle = color; ps.list.forEach((pt) => { const q = at(l, pt.d); ctx.beginPath(); ctx.arc(q[0], q[1], l.kind === 'tunnel' ? 1.5 : 1.9, 0, 6.2832); ctx.fill(); }); }
      label(l, color);
    });
    // discrete events travel as a single larger pip
    if (L.network && !st.reduced) {
      for (let i = S.pips.length - 1; i >= 0; i--) {
        const p = S.pips[i], l = SV.linkById[p.link];
        const dur = clamp((l.len * cam.k) / 240, 0.4, 1.1) * 1000, e = (now - p.at) / dur;
        if (e >= 1) { S.pips.splice(i, 1); continue; }
        if (e < 0) continue;
        const u = e < 0.5 ? 2 * e * e : 1 - Math.pow(-2 * e + 2, 2) / 2;
        const d = (p.dir > 0 ? u : 1 - u) * l.len, tail = (p.dir > 0 ? -1 : 1) * Math.min(l.len * 0.25, 26 / cam.k) * Math.sin(Math.PI * e);
        const q = at(l, d), q2 = at(l, clamp(d + tail, 0, l.len));
        ctx.strokeStyle = pal.net; ctx.lineWidth = 3; ctx.lineCap = 'round'; ctx.globalAlpha = 0.45;
        ctx.beginPath(); ctx.moveTo(q2[0], q2[1]); ctx.lineTo(q[0], q[1]); ctx.stroke(); ctx.globalAlpha = 1; ctx.lineCap = 'butt';
        ctx.beginPath(); ctx.arc(q[0], q[1], 3.6, 0, 6.2832); ctx.fillStyle = pal.net; ctx.fill();
        ctx.beginPath(); ctx.arc(q[0], q[1], 1.4, 0, 6.2832); ctx.fillStyle = pal.s3; ctx.fill();
      }
    } else S.pips.length = 0;
  }
  function brokenMark(l, color) {
    // a gap with a cross where the link would leave the installation
    const c = l.cross.find((x) => x.node.kind === 'boundary'); if (!c) return;
    const cx = X(c.x) + (c.horiz ? -13 : 0), cy = Y(c.y) + (c.horiz ? 0 : -13);
    ctx.fillStyle = pal.outside; ctx.fillRect(cx - 7, cy - 7, 14, 14);
    ctx.strokeStyle = color; ctx.lineWidth = 1.75; ctx.beginPath(); ctx.moveTo(cx - 4, cy - 4); ctx.lineTo(cx + 4, cy + 4); ctx.moveTo(cx + 4, cy - 4); ctx.lineTo(cx - 4, cy + 4); ctx.stroke();
  }
  function label(l, color) {
    // widest free span of the path (between frame crossings) carries the chip
    let best = null;
    for (let i = 1; i < l.pts.length; i++) {
      const p = l.pts[i - 1], q = l.pts[i], horiz = p[1] === q[1];
      const stops = [horiz ? p[0] : p[1], horiz ? q[0] : q[1]];
      l.cross.forEach((c) => { if (horiz ? c.horiz && c.y === p[1] : !c.horiz && c.x === p[0]) stops.push(horiz ? c.x : c.y); });
      stops.sort((a, b) => a - b);
      for (let j = 1; j < stops.length; j++) { const len = stops[j] - stops[j - 1]; if (!best || len > best.len) best = { len, mid: (stops[j] + stops[j - 1]) / 2, horiz, fixed: horiz ? p[1] : p[0] }; }
    }
    if (!best) return;
    const px = best.len * cam.k;
    const cx = X(best.horiz ? best.mid : best.fixed), cy = Y(best.horiz ? best.fixed : best.mid);
    const live = { pub: S.link.pub.rate.toFixed(0) + ' req/s', origin: S.link.origin.rate.toFixed(0) + ' req/s', tunnel: S.link.tunnel.rate < 1 ? 'idle' : S.link.tunnel.rate.toFixed(0) + ' kB/s', overlay: Math.round(S.latency) + ' ms' }[l.id];
    const cands = l.kind === 'local' ? [l.short] : [l.short + (live && l.kind !== 'public' ? ' · ' + live : ''), l.short, l.id === 'tunnel' ? 'tunnel' : l.id === 'overlay' ? 'overlay' : l.id === 'origin' ? ':443' : l.id === 'backup' ? 'none' : ''];
    ctx.font = f(500, 10.5, MONO);
    const min = l.kind === 'local' ? 110 : 0;
    let str = '', rot = false;
    for (let i = 0; i < cands.length && !str; i++) if (cands[i] && tw(cands[i]) + 16 <= px && px >= min) { str = cands[i]; rot = !best.horiz; }
    // a short vertical run still takes a short upright chip
    if (!str && !best.horiz) for (let i = 0; i < cands.length && !str; i++) if (cands[i] && tw(cands[i]) <= 58 && px >= Math.max(24, min)) str = cands[i];
    if (!str) return;
    const w = Math.round(tw(str) + 12), h = 16;
    ctx.save(); ctx.translate(Math.round(cx) + 0.5, Math.round(cy) + 0.5); if (rot) ctx.rotate(-Math.PI / 2);
    rrect(-Math.round(w / 2), -h / 2, w, h, 3);
    ctx.fillStyle = pal.s3; ctx.fill(); ctx.strokeStyle = color; ctx.globalAlpha = 0.55; ctx.lineWidth = 1; ctx.stroke(); ctx.globalAlpha = 1;
    text(str, 0, 0, ctx.font, color, 'center');
    ctx.restore();
  }

  /* ---------- warnings and selection ---------- */
  function markers() {
    S.warnings.forEach((w) => {
      const n = SV.nodes[w.node], r = srect(n.rect);
      if (!visible(r)) return;
      const cx = r.x + r.w - clamp(r.w * 0.04, 1, 4), cy = r.y + clamp(r.w * 0.04, 1, 4);
      if (w.state === 'resolved') {
        const age = (Date.now() - w.resolvedWall) / 1000; if (age > 45) return;
        alpha(clamp((45 - age) / 6, 0, 1), () => {
          ctx.beginPath(); ctx.arc(cx, cy, 8, 0, 6.2832); ctx.fillStyle = pal.ok; ctx.fill();
          ctx.strokeStyle = pal.s3; ctx.lineWidth = 1.75; ctx.beginPath(); ctx.moveTo(cx - 3.5, cy); ctx.lineTo(cx - 1, cy + 2.8); ctx.lineTo(cx + 3.8, cy - 2.8); ctx.stroke();
          if (age < 1.6 && !st.reduced) { ctx.strokeStyle = pal.ok; ctx.lineWidth = 1.5; ctx.globalAlpha *= 1 - age / 1.6; ctx.beginPath(); ctx.arc(cx, cy, 8 + age * 14, 0, 6.2832); ctx.stroke(); }
        });
        return;
      }
      ctx.beginPath(); ctx.arc(cx, cy, 10.5, 0, 6.2832); ctx.fillStyle = pal.s3; ctx.fill();
      tri(cx, cy + 0.5, 8, pal.warn);
      ctx.fillStyle = pal.onwarn; ctx.fillRect(cx - 0.9, cy - 3.5, 1.8, 5.2); ctx.fillRect(cx - 0.9, cy + 3, 1.8, 1.8);
    });
  }
  function rings() {
    const draw = (id, strong) => {
      const n = SV.nodes[id]; if (!n || n.kind === 'boundary') return; if (n.layer && !st.layers[n.layer]) return;
      const r = srect(n.rect), o = strong ? 4 : 3;
      rrect(r.x - o, r.y - o, r.w + 2 * o, r.h + 2 * o, clamp(10 * cam.k, 4, 12) + o);
      ctx.strokeStyle = pal.ink; ctx.lineWidth = strong ? 2 : 1; ctx.globalAlpha = strong ? 1 : 0.5; ctx.stroke(); ctx.globalAlpha = 1;
    };
    if (st.hover && st.hover !== st.sel) draw(st.hover, false);
    if (st.sel) draw(st.sel, true);
  }

  /* ---------- hit testing ---------- */
  R.pick = function (sx, sy) {
    const wx = R.wx(sx), wy = R.wy(sy);
    if (st.layers.network || st.layers.backup) {
      let best = null, bd = 9;
      SV.links.forEach((l) => {
        if (l.layer ? !st.layers[l.layer] : !st.layers.network) return;
        for (let i = 1; i < l.pts.length; i++) {
          const ax = X(l.pts[i - 1][0]), ay = Y(l.pts[i - 1][1]), bx = X(l.pts[i][0]), by = Y(l.pts[i][1]);
          const len2 = (bx - ax) * (bx - ax) + (by - ay) * (by - ay) || 1;
          const t = clamp(((sx - ax) * (bx - ax) + (sy - ay) * (by - ay)) / len2, 0, 1);
          const d = Math.hypot(sx - (ax + (bx - ax) * t), sy - (ay + (by - ay) * t));
          if (d < bd) { bd = d; best = l; }
        }
      });
      if (best) return { link: best.id };
    }
    let hit = null;
    (function walk(list) {
      list.forEach((n) => {
        if (n.layer && !st.layers[n.layer]) return;
        const r = n.rect;
        if (wx >= r.x && wx <= r.x + r.w && wy >= r.y && wy <= r.y + r.h) { if (n.kind !== 'boundary') hit = n.id; walk(n.children); }
      });
    })([SV.nodes.net, SV.nodes.cdn, SV.nodes.offsite, SV.nodes.install]);
    return hit ? { node: hit } : null;
  };

  /* ---------- key plan ---------- */
  R.keyplan = function (c) {
    const g = c.getContext('2d'), d = Math.min(window.devicePixelRatio || 1, 3);
    const cw = c.clientWidth, ch = c.clientHeight;
    if (c.width !== Math.round(cw * d)) { c.width = Math.round(cw * d); c.height = Math.round(ch * d); }
    g.setTransform(d, 0, 0, d, 0, 0); g.clearRect(0, 0, cw, ch);
    const b = SV.bounds, k = Math.min((cw - 6) / b.w, (ch - 6) / b.h);
    const ox = (cw - b.w * k) / 2 - b.x * k, oy = (ch - b.h * k) / 2 - b.y * k;
    R.keyT = { k, ox, oy };
    const rect = (r) => [Math.round(r.x * k + ox) + 0.5, Math.round(r.y * k + oy) + 0.5, Math.round(r.w * k), Math.round(r.h * k)];
    g.lineWidth = 1;
    let q = rect(SV.nodes.install.rect); g.setLineDash([3, 2]); g.strokeStyle = pal.ink3; g.strokeRect(q[0], q[1], q[2], q[3]); g.setLineDash([]);
    ['app', 'ws', 'laptop', 'vm', 'net', 'cdn'].forEach((id) => { q = rect(SV.nodes[id].rect); g.fillStyle = id === 'vm' ? pal.s3 : pal.s1; g.fillRect(q[0], q[1], q[2], q[3]); g.strokeStyle = pal.ink2; g.strokeRect(q[0], q[1], q[2], q[3]); });
    if (st.layers.warnings) S.openWarnings().forEach((w) => { const r = SV.nodes[w.node].rect; g.fillStyle = pal.warn; g.fillRect(Math.round((r.x + r.w / 2) * k + ox) - 2, Math.round((r.y + r.h / 2) * k + oy) - 2, 4, 4); });
    // current view
    const vx0 = clamp(R.wx(SV.safe.l) * k + ox, 1, cw - 1), vy0 = clamp(R.wy(SV.safe.t) * k + oy, 1, ch - 1);
    const vx1 = clamp(R.wx(W - SV.safe.r) * k + ox, 1, cw - 1), vy1 = clamp(R.wy(H - SV.safe.b) * k + oy, 1, ch - 1);
    g.fillStyle = pal.ink; g.globalAlpha = 0.08; g.fillRect(vx0, vy0, vx1 - vx0, vy1 - vy0); g.globalAlpha = 1;
    g.strokeStyle = pal.ink; g.lineWidth = 1.5; g.strokeRect(Math.round(vx0) + 0.5, Math.round(vy0) + 0.5, Math.round(vx1 - vx0), Math.round(vy1 - vy0));
  };
})();
