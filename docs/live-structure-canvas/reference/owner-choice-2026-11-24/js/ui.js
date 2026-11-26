/* Camera, gestures, layer toggles, breadcrumb, key plan and the detail panel. */
(function () {
  'use strict';
  const SV = window.SV, S = SV.sim, R = SV.render, cam = SV.cam, st = SV.state, N = SV.nodes;
  const $ = (s) => document.querySelector(s);
  const cv = $('#cv'), stage = $('#stage'), panel = $('#panel'), body = $('#panel-body'), keyCv = $('#key'), tblock = $('#titleblock');
  const clamp = (v, a, b) => Math.max(a, Math.min(b, v));
  const esc = (s) => String(s).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
  const pct = R.fmt.pct, gb = R.fmt.gb;
  SV.safe = { l: 0, t: 0, r: 0, b: 0 };
  let W = 0, H = 0, phone = false, sheet = 'peek', kMin = 0.2, kMax = 4.2, loadWall = 0;
  const hp = new URLSearchParams(location.hash.slice(1));
  st.reduced = window.matchMedia && matchMedia('(prefers-reduced-motion: reduce)').matches;

  /* ================= camera ================= */
  let fly = null, zoomT = null, inertia = null;
  function stopAnim() { fly = null; zoomT = null; inertia = null; }
  function sheetH() { return sheet === 'open' ? Math.round(Math.min(H * 0.58, 520)) : 92; }
  function measure(keep) {
    const ox = cam.ox, oy = cam.oy;
    W = stage.clientWidth; H = stage.clientHeight; phone = W <= 720;
    const s = SV.safe;
    if (phone) { stage.style.setProperty('--sheet-h', sheetH() + 'px'); s.l = 0; s.r = 0; s.t = 74; s.b = sheetH() + 6; }
    else { s.l = 0; s.r = panel.offsetWidth; s.t = 58; s.b = tblock.offsetHeight + 26; }
    cam.ox = s.l + (W - s.l - s.r) / 2; cam.oy = s.t + (H - s.t - s.b) / 2;
    if (keep) { cam.x += (cam.ox - ox) / cam.k; cam.y += (cam.oy - oy) / cam.k; }
    R.resize(W, H);
  }
  function viewFor(rect, maxPx, pad) {
    const s = SV.safe, sw = W - s.l - s.r, sh = H - s.t - s.b;
    let k = Math.min(sw / rect.w, sh / rect.h) * (pad || 0.9);
    if (maxPx) k = Math.min(k, maxPx / rect.w);
    return { x: rect.x + rect.w / 2, y: rect.y + rect.h / 2, k: clamp(k, kMin, kMax) };
  }
  function go(v, instant) {
    stopAnim();
    if (instant || st.reduced) { cam.x = v.x; cam.y = v.y; cam.k = v.k; return; }
    const dist = Math.hypot(v.x - cam.x, v.y - cam.y) * cam.k;
    fly = { x0: cam.x, y0: cam.y, k0: cam.k, x1: v.x, y1: v.y, k1: v.k, t0: performance.now(), dur: clamp(260 + 240 * Math.abs(Math.log(v.k / cam.k)) + dist / 5, 280, 900) };
  }
  function fitAll(instant) { go(viewFor(SV.bounds, 0, 0.97), instant); }
  function fitNode(id, instant) {
    const n = N[id]; if (!n || n.kind === 'boundary') return fitAll(instant);
    const leaf = !n.children.length;
    go(viewFor(n.rect, leaf ? (phone ? W - 24 : n.kind === 'disk' ? 760 : 640) : 0, leaf ? (phone ? 0.94 : 0.86) : 0.92), instant);
  }
  function zoomAt(sx, sy, factor, tc) {
    fly = null; inertia = null;
    const k = clamp((zoomT ? zoomT.k : cam.k) * factor, kMin, kMax);
    zoomT = { k, sx, sy, wx: R.wx(sx), wy: R.wy(sy), tc: tc || 0.085 };
    if (st.reduced) { applyZoom(k); zoomT = null; }
  }
  function applyZoom(k) { cam.k = k; cam.x = zoomT.wx - (zoomT.sx - cam.ox) / k; cam.y = zoomT.wy - (zoomT.sy - cam.oy) / k; }
  function stepCamera(t, dt) {
    if (fly) {
      const u = clamp((t - fly.t0) / fly.dur, 0, 1), e = u < 0.5 ? 4 * u * u * u : 1 - Math.pow(-2 * u + 2, 3) / 2;
      const k = fly.k0 * Math.pow(fly.k1 / fly.k0, e);
      if (Math.abs(fly.k1 - fly.k0) / fly.k0 > 0.02) {
        // zoom about the one world point that keeps its screen position: no swooping
        const fx = (fly.x0 * fly.k0 - fly.x1 * fly.k1) / (fly.k0 - fly.k1), fy = (fly.y0 * fly.k0 - fly.y1 * fly.k1) / (fly.k0 - fly.k1);
        cam.x = fx - ((fx - fly.x0) * fly.k0) / k; cam.y = fy - ((fy - fly.y0) * fly.k0) / k;
      } else { cam.x = fly.x0 + (fly.x1 - fly.x0) * e; cam.y = fly.y0 + (fly.y1 - fly.y0) * e; }
      cam.k = k;
      if (u >= 1) { cam.x = fly.x1; cam.y = fly.y1; cam.k = fly.k1; fly = null; }
    } else if (zoomT) {
      const lk = Math.log(cam.k) + (Math.log(zoomT.k) - Math.log(cam.k)) * (1 - Math.exp(-dt / zoomT.tc));
      applyZoom(Math.exp(lk));
      if (Math.abs(Math.log(zoomT.k / cam.k)) < 0.002) { applyZoom(zoomT.k); zoomT = null; }
    } else if (inertia) {
      cam.x -= (inertia.vx * dt) / cam.k; cam.y -= (inertia.vy * dt) / cam.k;
      const d = Math.exp(-dt / 0.22); inertia.vx *= d; inertia.vy *= d;
      if (Math.hypot(inertia.vx, inertia.vy) < 12) inertia = null;
    }
    const b = SV.bounds;
    cam.x = clamp(cam.x, b.x, b.x + b.w); cam.y = clamp(cam.y, b.y, b.y + b.h);
  }
  function relayout(force) {
    const s = SV.safe;
    const mode = phone || (W - s.r) / H < 0.82 ? 'tall' : 'wide';
    if (mode !== SV.mode || force) {
      SV.layout(mode);
      kMin = viewFor(SV.bounds, 0, 0.97).k * 0.72; kMax = 4.2;
      return true;
    }
    kMin = Math.min(kMin, viewFor(SV.bounds, 0, 0.97).k * 0.72);
    return false;
  }

  /* ================= selection ================= */
  function select(id, opts) {
    opts = opts || {};
    st.sel = id; st.selLink = null;
    if (phone) setSheet(id === 'install' && !opts.open ? 'peek' : 'open', true);
    renderPanel();
    if (id !== 'install' && !opts.stay) ensureVisible(id);
    writeHash();
  }
  function selectLink(id) {
    st.selLink = id; st.sel = null;
    if (phone) setSheet('open', true);
    renderPanel(); writeHash();
  }
  function ensureVisible(id) {
    const n = N[id]; if (!n) return;
    const r = R.srect(n.rect), s = SV.safe, m = 14;
    const x0 = s.l + m, x1 = W - s.r - m, y0 = s.t + m, y1 = H - s.b - m;
    if (r.w > x1 - x0 || r.h > y1 - y0) {
      const cx = r.x + r.w / 2, cy = r.y + r.h / 2;
      if (cx < x0 || cx > x1 || cy < y0 || cy > y1) fitNode(id);
      return;
    }
    let dx = 0, dy = 0;
    if (r.x < x0) dx = x0 - r.x; else if (r.x + r.w > x1) dx = x1 - (r.x + r.w);
    if (r.y < y0) dy = y0 - r.y; else if (r.y + r.h > y1) dy = y1 - (r.y + r.h);
    if (dx || dy) go({ x: cam.x - dx / cam.k, y: cam.y - dy / cam.k, k: cam.k });
  }
  function setSheet(state, quiet) {
    if (sheet === state) return;
    sheet = state; panel.dataset.sheet = state;
    measure(true);
    if (!quiet && st.sel && st.sel !== 'install') ensureVisible(st.sel);
  }

  /* ================= pointer, wheel, keyboard ================= */
  const ptrs = new Map();
  let drag = null, pinch = null, lastTap = null, padUntil = 0;
  const pos = (e) => { const r = cv.getBoundingClientRect(); return { x: e.clientX - r.left, y: e.clientY - r.top }; };
  cv.addEventListener('pointerdown', (e) => {
    if (e.pointerType === 'mouse' && e.button !== 0) return;
    cv.setPointerCapture(e.pointerId);
    const p = pos(e); ptrs.set(e.pointerId, p); stopAnim();
    if (ptrs.size === 1) drag = { x0: p.x, y0: p.y, x: p.x, y: p.y, t0: performance.now(), lt: performance.now(), moved: false, vx: 0, vy: 0 };
    else if (ptrs.size === 2) { const a = [...ptrs.values()]; pinch = { d: Math.hypot(a[0].x - a[1].x, a[0].y - a[1].y) || 1, k: cam.k, wx: R.wx((a[0].x + a[1].x) / 2), wy: R.wy((a[0].y + a[1].y) / 2) }; if (drag) drag.moved = true; }
  });
  cv.addEventListener('pointermove', (e) => {
    const p = pos(e);
    if (!ptrs.has(e.pointerId)) { if (e.pointerType === 'mouse') hover(p); return; }
    ptrs.set(e.pointerId, p);
    if (pinch && ptrs.size >= 2) {
      const a = [...ptrs.values()], mx = (a[0].x + a[1].x) / 2, my = (a[0].y + a[1].y) / 2;
      const k = clamp((pinch.k * Math.hypot(a[0].x - a[1].x, a[0].y - a[1].y)) / pinch.d, kMin, kMax);
      cam.k = k; cam.x = pinch.wx - (mx - cam.ox) / k; cam.y = pinch.wy - (my - cam.oy) / k;
      return;
    }
    if (!drag) return;
    const dx = p.x - drag.x, dy = p.y - drag.y, t = performance.now(), dt = Math.max(1, t - drag.lt) / 1000;
    if (!drag.moved && Math.hypot(p.x - drag.x0, p.y - drag.y0) > 5) { drag.moved = true; cv.classList.add('dragging'); }
    if (drag.moved) {
      cam.x -= dx / cam.k; cam.y -= dy / cam.k;
      drag.vx = drag.vx * 0.6 + (dx / dt) * 0.4; drag.vy = drag.vy * 0.6 + (dy / dt) * 0.4;
    }
    drag.x = p.x; drag.y = p.y; drag.lt = t;
  });
  function up(e) {
    if (!ptrs.has(e.pointerId)) return;
    ptrs.delete(e.pointerId);
    const t = performance.now();
    if (pinch) { pinch = null; if (ptrs.size === 1) { const p = [...ptrs.values()][0]; drag = { x0: p.x, y0: p.y, x: p.x, y: p.y, t0: t, lt: t, moved: true, vx: 0, vy: 0 }; } return; }
    if (!drag || ptrs.size) return;
    cv.classList.remove('dragging');
    if (!drag.moved && e.type === 'pointerup') tap(drag.x0, drag.y0);
    else if (drag.moved && t - drag.lt < 70 && Math.hypot(drag.vx, drag.vy) > 120 && !st.reduced) inertia = { vx: clamp(drag.vx, -3000, 3000), vy: clamp(drag.vy, -3000, 3000) };
    drag = null;
  }
  cv.addEventListener('pointerup', up); cv.addEventListener('pointercancel', up);
  cv.addEventListener('pointerleave', () => { if (st.hover || st.hoverLink) { st.hover = null; st.hoverLink = null; cv.classList.remove('pointing'); } });

  function hover(p) {
    const h = R.pick(p.x, p.y);
    st.hover = h && h.node ? h.node : null; st.hoverLink = h && h.link ? h.link : null;
    cv.classList.toggle('pointing', !!h);
  }
  function tap(x, y) {
    const h = R.pick(x, y), t = performance.now();
    const dbl = lastTap && t - lastTap.t < 340 && Math.hypot(x - lastTap.x, y - lastTap.y) < 28;
    lastTap = dbl ? null : { t, x, y };
    if (dbl) { if (h && h.node) fitNode(h.node); else zoomAt(x, y, 1.9); return; }
    if (h && h.link) selectLink(h.link);
    else if (h && h.node) select(h.node);
    else select('install');
  }
  cv.addEventListener('wheel', (e) => {
    e.preventDefault();
    const p = pos(e), t = performance.now();
    const unit = e.deltaMode === 1 ? 33 : e.deltaMode === 2 ? 300 : 1;
    const dy = e.deltaY * unit, dx = e.deltaX * unit;
    if (e.ctrlKey || e.metaKey) { zoomAt(p.x, p.y, Math.exp(-clamp(dy, -60, 60) * 0.012), 0.03); return; }
    // A wheel notch arrives as large whole steps with no sideways part; a trackpad sends small mixed deltas.
    const notch = e.deltaMode === 1 || (dx === 0 && (e.wheelDeltaY !== undefined ? Math.abs(e.wheelDeltaY) >= 120 && Math.abs(e.wheelDeltaY) % 120 === 0 : Math.abs(dy) >= 50));
    if (notch && t > padUntil) { zoomAt(p.x, p.y, Math.exp(-clamp(dy / 100, -3, 3) * 0.24)); return; }
    padUntil = t + 250; stopAnim();
    cam.x += dx / cam.k; cam.y += dy / cam.k;
  }, { passive: false });
  // Safari trackpad pinch
  let gk = 1;
  cv.addEventListener('gesturestart', (e) => { e.preventDefault(); gk = cam.k; });
  cv.addEventListener('gesturechange', (e) => { e.preventDefault(); const p = pos(e); zoomT = { k: clamp(gk * e.scale, kMin, kMax), sx: p.x, sy: p.y, wx: R.wx(p.x), wy: R.wy(p.y), tc: 0.03 }; });

  window.addEventListener('keydown', (e) => {
    const a = document.activeElement;
    if (a && /^(INPUT|TEXTAREA|SELECT)$/.test(a.tagName)) return;
    if (e.metaKey || e.ctrlKey || e.altKey) return;
    const s = SV.safe, cx = cam.ox, cy = cam.oy, step = 90 / cam.k;
    const onButton = a && a.tagName === 'BUTTON';
    switch (e.key) {
      case '+': case '=': zoomAt(cx, cy, 1.5); break;
      case '-': case '_': zoomAt(cx, cy, 1 / 1.5); break;
      case '0': case 'f': fitAll(); break;
      case 'ArrowLeft': go({ x: cam.x - step, y: cam.y, k: cam.k }); break;
      case 'ArrowRight': go({ x: cam.x + step, y: cam.y, k: cam.k }); break;
      case 'ArrowUp': go({ x: cam.x, y: cam.y - step, k: cam.k }); break;
      case 'ArrowDown': go({ x: cam.x, y: cam.y + step, k: cam.k }); break;
      case 'Escape': { const f = focusId(); const par = f !== 'install' ? N[f].parent : null; if (st.sel !== 'install' || st.selLink) select('install', { stay: true }); else if (par) fitNode(par); else fitAll(); break; }
      case 'Enter': if (onButton) return; if (st.sel) fitNode(st.sel); break;
      case ' ': if (onButton) return; togglePause(); break;
      case '1': case '2': case '3': case '4': case '5': toggleLayer(['perf', 'network', 'storage', 'backup', 'warnings'][+e.key - 1]); break;
      default: return;
    }
    e.preventDefault(); void s;
  });

  /* ================= chrome: layers, zoom, theme, live ================= */
  function toggleLayer(name, value) {
    st.layers[name] = value === undefined ? !st.layers[name] : value;
    document.querySelectorAll('#layers button').forEach((b) => b.setAttribute('aria-pressed', String(!!st.layers[b.dataset.layer])));
    if (st.sel && N[st.sel] && N[st.sel].layer && !st.layers[N[st.sel].layer]) select('install', { stay: true });
    if (st.selLink && SV.linkById[st.selLink].layer && !st.layers[SV.linkById[st.selLink].layer]) select('install', { stay: true });
    legend(); if (!phone) measure(true);
    writeHash();
  }
  $('#layers').addEventListener('click', (e) => { const b = e.target.closest('button'); if (b) toggleLayer(b.dataset.layer); });
  $('#z-in').addEventListener('click', () => zoomAt(cam.ox, cam.oy, 1.6));
  $('#z-out').addEventListener('click', () => zoomAt(cam.ox, cam.oy, 1 / 1.6));
  $('#z-fit').addEventListener('click', () => fitAll());
  $('#theme').addEventListener('click', () => {
    const t = document.documentElement.getAttribute('data-theme') === 'dark' ? 'light' : 'dark';
    document.documentElement.setAttribute('data-theme', t); R.palette(); legend(); renderPanel(); writeHash(true);
  });
  function togglePause() {
    S.paused = !S.paused;
    $('#live').setAttribute('aria-pressed', String(!S.paused));
    $('#live-label').textContent = S.paused ? 'Paused' : 'Live';
  }
  $('#live').addEventListener('click', togglePause);

  let themeExplicit = hp.has('theme');
  function writeHash(theme) {
    if (theme) themeExplicit = true;
    const q = ['layers=' + Object.keys(st.layers).filter((k) => st.layers[k]).join(',')];
    if (st.sel && st.sel !== 'install') q.push('sel=' + st.sel);
    if (st.selLink) q.push('link=' + st.selLink);
    if (themeExplicit) q.push('theme=' + document.documentElement.getAttribute('data-theme'));
    try { history.replaceState(null, '', '#' + q.join('&')); } catch (err) { /* file: URLs in some browsers */ }
  }

  /* ================= breadcrumb: where the zoom currently is ================= */
  function focusId() {
    const s = SV.safe, sw = W - s.l - s.r, sh = H - s.t - s.b;
    let cur = 'install';
    (function walk(n) {
      for (let i = 0; i < n.children.length; i++) {
        const c = n.children[i], r = c.rect;
        if (cam.x >= r.x && cam.x <= r.x + r.w && cam.y >= r.y && cam.y <= r.y + r.h && (r.w * cam.k >= sw * (c.children.length ? 0.78 : 0.56) || r.h * cam.k >= sh * (c.children.length ? 0.78 : 0.56))) { cur = c.id; walk(c); return; }
      }
    })(N.install);
    return cur;
  }
  let crumbKey = '';
  function crumbs() {
    const id = focusId();
    if (id === crumbKey) return; crumbKey = id;
    const path = SV.path(id);
    const html = path.map((n, i) => (i ? '<span class="sep">/</span>' : '') + '<button type="button" data-go="' + n.id + '"' + (i === path.length - 1 ? ' aria-current="true"' : '') + '>' + esc(n.kind === 'boundary' ? 'Installation' : n.name) + '</button>').join('');
    $('#crumbs').innerHTML = html;
    const cut = path.length > 3 ? path.slice(-2) : path;
    $('#crumbs-float').innerHTML = (cut !== path ? '<button type="button" data-go="' + path[path.length - 3].id + '" aria-label="Up">…</button><span class="sep">/</span>' : '') + cut.map((n, i) => (i ? '<span class="sep">/</span>' : '') + '<button type="button" data-go="' + n.id + '"' + (i === cut.length - 1 ? ' aria-current="true"' : '') + '>' + esc(n.kind === 'boundary' ? 'Installation' : n.name) + '</button>').join('');
  }
  [$('#crumbs'), $('#crumbs-float')].forEach((el) => el.addEventListener('click', (e) => { const b = e.target.closest('button'); if (b) fitNode(b.dataset.go); }));

  /* ================= key plan ================= */
  function keyMove(e) {
    const r = keyCv.getBoundingClientRect(), t = R.keyT; if (!t) return;
    stopAnim(); cam.x = (e.clientX - r.left - t.ox) / t.k; cam.y = (e.clientY - r.top - t.oy) / t.k;
  }
  keyCv.addEventListener('pointerdown', (e) => { keyCv.setPointerCapture(e.pointerId); keyMove(e); });
  keyCv.addEventListener('pointermove', (e) => { if (e.buttons || e.pointerType === 'touch') keyMove(e); });

  /* ================= glyphs and legend ================= */
  function glyph(kind) {
    const a = 'fill="none" stroke-width="1.25"';
    const g = {
      boundary: '<rect x="1" y="1" width="24" height="12" rx="2" ' + a + ' stroke="var(--c-f1)" stroke-dasharray="6 2 1 2"/>',
      machine: '<rect x="1" y="1" width="24" height="12" rx="3" fill="var(--c-s1)" stroke="var(--c-f1)" stroke-width="1.5"/>',
      vm: '<rect x="1" y="1" width="24" height="12" rx="2.5" fill="var(--c-s2)" stroke="var(--c-f2)" stroke-width="1"/><rect x="3.5" y="3.5" width="19" height="7" rx="1" fill="none" stroke="var(--c-f2)" stroke-width="1"/>',
      stack: '<rect x="1" y="1" width="24" height="12" rx="2" ' + a + ' stroke="var(--c-f2)" stroke-dasharray="3 2.5"/>',
      container: '<rect x="1" y="1" width="24" height="12" rx="2.5" fill="var(--c-s3)" stroke="var(--c-f2)" stroke-width="1"/>',
      service: '<path d="M6 1H25V13H1V6Z" fill="var(--c-s3)" stroke="var(--c-f2)" stroke-width="1"/>',
      disk: '<rect x="1" y="3" width="24" height="8" rx="4" fill="var(--c-s3)" stroke="var(--c-f2)" stroke-width="1"/><rect x="4" y="6" width="13" height="2" fill="var(--c-ink2)"/>',
      external: '<rect x="1" y="1" width="24" height="12" rx="2" ' + a + ' stroke="var(--c-f2)" stroke-dasharray="1.5 2.5"/>',
      ghost: '<rect x="1" y="1" width="24" height="12" rx="2" ' + a + ' stroke="var(--c-backup)" stroke-dasharray="3 2.5"/>',
      public: '<path d="M1 7H21" stroke="var(--c-net)" stroke-width="1.6" fill="none"/><path d="M25 7l-6-3.5v7z" fill="var(--c-net)"/>',
      tunnel: '<path d="M1 4.5H25M1 9.5H25" stroke="var(--c-net)" stroke-width="1.1" fill="none"/><rect x="1" y="4.5" width="24" height="5" fill="var(--c-net)" opacity=".15"/>',
      overlay: '<path d="M3 7H23" stroke="var(--c-net)" stroke-width="1.5" stroke-dasharray="5 3" fill="none"/>',
      local: '<path d="M1 7H25" stroke="var(--c-net)" stroke-width="1.2" fill="none"/>',
      backup: '<path d="M1 7H25" stroke="var(--c-backup)" stroke-width="1.5" stroke-dasharray="2 4" fill="none"/>',
      port: '<path d="M1 7H25" stroke="var(--c-net)" stroke-width="1.2" fill="none"/><rect x="10" y="4" width="6" height="6" fill="var(--c-s3)" stroke="var(--c-net)" stroke-width="1.25"/>',
      slot: '<rect x="1" y="2.5" width="9" height="9" fill="var(--c-perf)"/><rect x="12" y="2.5" width="9" height="9" fill="var(--c-track)"/>',
      held: '<path d="M2 11.5l9-9M2 7l4.5-4.5M6.5 11.5L11 7" stroke="var(--c-warn)" stroke-width="1"/><rect x="1.5" y="2.5" width="9" height="9" fill="none" stroke="var(--c-warn)" stroke-width="1"/>',
      data: '<rect x="1" y="4" width="24" height="6" fill="var(--c-storage)" opacity=".85"/>',
      snap: '<rect x="1" y="4" width="24" height="6" fill="var(--c-backup)" opacity=".28"/><path d="M3 10l6-6M9 10l6-6M15 10l6-6M21 10l4-4" stroke="var(--c-backup)" stroke-width="1"/>',
      nocopy: '<rect x="1.5" y="3" width="23" height="8" fill="none" stroke="var(--c-backup)" stroke-width="1.25" stroke-dasharray="3 2"/>',
      warnm: '<path d="M13 1.5l7 11.5H6z" fill="var(--c-warn)"/><path d="M13 5.5v4M13 10.6v1.2" stroke="var(--c-onwarn)" stroke-width="1.4"/>',
    }[kind] || '';
    return '<svg viewBox="0 0 26 14" aria-hidden="true">' + g + '</svg>';
  }
  // The legend follows the layers: it only explains marks that are on the plan right now.
  const LEGEND = [['machine', 'machine'], ['vm', 'virtual machine'], ['stack', 'container stack'], ['container', 'container'], ['service', 'host service'], ['external', 'outside'], ['boundary', 'system boundary']];
  const LAYER_LEGEND = {
    perf: [['slot', 'slot in use, free'], ['held', 'slot held back']],
    network: [['public', 'public route'], ['tunnel', 'tunnel'], ['overlay', 'overlay network'], ['port', 'frame crossing']],
    storage: [['data', 'data volume']],
    backup: [['snap', 'snapshot, same disk'], ['nocopy', 'no off-host copy']],
    warnings: [['warnm', 'warning']],
  };
  function legendHTML() {
    let items = LEGEND.slice();
    Object.keys(LAYER_LEGEND).forEach((k) => { if (st.layers[k]) items = items.concat(LAYER_LEGEND[k]); });
    return items.map((l) => '<span class="lg">' + glyph(l[0]) + l[1] + '</span>').join('');
  }
  function legend() { const h = legendHTML(); $('#legend').innerHTML = h; const inl = body.querySelector('.legend-inline'); if (inl) inl.innerHTML = h; }

  /* ================= detail panel ================= */
  const wall = (t) => new Date(loadWall + t * 1000).toLocaleTimeString('en-GB');
  function since(w) {
    if (w.state === 'resolved') { const s = Math.max(0, Math.round(S.t - w.resolvedAt)); return 'resolved ' + (s < 90 ? s + ' s' : Math.round(s / 60) + ' min') + ' ago'; }
    const m = Math.round(w.sinceMin + Math.max(0, S.t) / 60);
    return 'for ' + (m >= 2880 ? Math.round(m / 1440) + ' days' : m >= 120 ? Math.floor(m / 60) + ' h ' + (m % 60) + ' min' : m + ' min');
  }
  function sparkSVG(key, max) {
    const a = S.hist[key] || []; if (a.length < 2) return '<svg></svg>';
    const mx = max || Math.max(1e-6, Math.max.apply(null, a)) * 1.18; let d = '';
    for (let i = 0; i < a.length; i++) d += (i ? 'L' : 'M') + ((i / (a.length - 1)) * 100).toFixed(1) + ' ' + (23 - clamp(a[i] / mx, 0, 1) * 21).toFixed(1);
    return '<svg viewBox="0 0 100 24" preserveAspectRatio="none" aria-hidden="true"><path class="ar" d="' + d + 'L100 24L0 24Z"/><path class="ln" d="' + d + '"/></svg>';
  }
  const metric = (label, value, small, key, max, cls) => '<div class="metric ' + (cls || '') + '"><span class="m-l">' + label + '</span><span class="m-v">' + value + (small ? ' <small>' + small + '</small>' : '') + '</span>' + (key ? sparkSVG(key, max) : '<span></span>') + '</div>';
  const sec = (title, inner, color, note) => '<section class="sec"><h3>' + (color ? '<i style="--lc:var(--c-' + color + ')"></i>' : '') + title + (note ? '<small>' + note + '</small>' : '') + '</h3>' + inner + '</section>';
  function descendants(n, out) { out = out || []; n.children.forEach((c) => { out.push(c); descendants(c, out); }); return out; }
  function rateText(id) {
    const l = SV.linkById[id], v = S.link[id].rate;
    if (l.kind === 'backup') return 'none';
    if (id === 'overlay') return Math.round(S.latency) + ' ms';
    if (id === 'tunnel') return v < 1 ? 'idle' : v.toFixed(0) + ' kB/s';
    if (l.unit === 'events/min') return v.toFixed(0) + ' /min';
    return v.toFixed(0) + ' ' + l.unit;
  }
  function stateText(id) {
    const n = N[id], m = S.m[id];
    const open = S.warningsFor(id).filter((w) => w.state === 'open');
    let txt;
    if (id === 'install') { const c = S.openWarnings().length; return '<span class="dot ' + (c ? 'warn' : '') + '"></span>' + (c ? c + ' open warning' + (c > 1 ? 's' : '') : 'No open warnings') + ' · ' + S.busy + ' of ' + S.capTotal + ' slots · ' + (S.queues.coding + S.queues.review + S.queues.gate) + ' queued'; }
    if (n.slots) txt = m.down ? 'Running, both slots held back' : m.restart ? 'Restarting' : 'Running · ' + S.used(id) + ' of ' + n.slots + ' slots in use';
    else if (n.gate) txt = m.job ? 'Verifying a batch of ' + m.job.batch : 'Idle, waiting for a batch';
    else if (n.kind === 'disk') return '<span class="dot ' + (open.length ? 'warn' : '') + '"></span>' + pct(m.frac) + ' full · ' + (n.capacity - m.used).toFixed(0) + ' GB free' + (open.length ? ' · above the 80% threshold' : '');
    else if (n.kind === 'ghost') return '<span class="dot off"></span>Not configured';
    else if (n.kind === 'external') txt = 'Outside the installation';
    else if (n.light) txt = 'Connected over the overlay · ' + Math.round(S.latency) + ' ms';
    else if (n.kind === 'machine' || n.kind === 'vm' || n.kind === 'stack') { const c = descendants(n).concat([n]).reduce((a, x) => a + S.warningsFor(x.id).filter((w) => w.state === 'open').length, 0); return '<span class="dot ' + (c ? 'warn' : '') + '"></span>' + (c ? c + ' open warning' + (c > 1 ? 's' : '') + ' inside' : 'Healthy') + (m.cpu != null && (n.perfHost || n.hostCpu) ? ' · cpu ' + pct(m.cpu) : ''); }
    else txt = 'Running';
    return '<span class="dot ' + (open.length ? 'warn' : '') + '"></span>' + (open.length ? esc(open[0].title) + ' · ' : '') + txt;
  }

  // live blocks: re-rendered every tick, only written to the DOM when their markup changed
  const LIVE = {
    state: (id) => stateText(id),
    kpis: () => {
      const q = S.queues, c = S.openWarnings().length;
      return '<div class="kpi"><span>Slots in use</span><strong>' + S.busy + '<small> / ' + S.capTotal + '</small></strong><em>' + (S.m.r3.held ? '2 held back' : 'all usable') + '</em></div>' +
        '<div class="kpi"><span>Queued</span><strong>' + (q.coding + q.review + q.gate) + '</strong><em>in 3 queues</em></div>' +
        '<div class="kpi"><span>Warnings</span><strong>' + c + '</strong><em>' + (c ? 'none critical' : 'all clear') + '</em></div>';
    },
    trend: () => metric('Busy slots', S.busy, 'of ' + S.capTotal, 'busy', 23) + metric('Queue', S.queues.coding + S.queues.review + S.queues.gate, 'tasks waiting', 'q.total', 0),
    warnlist: () => '<ul class="rows">' + S.warnings.map((w) => '<li><button type="button" data-sel="' + w.node + '"><i class="' + (w.state === 'open' ? 'tri' : 'okm') + '"></i><span class="r-t"><strong>' + esc(w.title) + '</strong><span>' + esc(SV.path(w.node).map((n) => n.name).slice(1).join(' / ')) + '</span></span><span class="r-v">' + since(w) + '</span></button></li>').join('') + '</ul>',
    events: () => '<ul class="events">' + S.events.slice(-5).reverse().map((e) => '<li><time>' + wall(e.t) + '</time><span class="' + e.sev + '">' + esc(e.text) + '</span></li>').join('') + '</ul>',
    warn: (id) => S.warningsFor(id).map((w) => '<div class="wcard ' + (w.state === 'resolved' ? 'ok' : '') + '"><h4>' + esc(w.state === 'resolved' ? w.title + ': fixed' : w.title) + '<small>' + since(w) + '</small></h4><p>' + esc(w.state === 'resolved' ? 'The dependency was installed and the container restarted. Both slots take reviews again.' : w.text) + '</p>' + (w.state === 'resolved' ? '<button type="button" class="btn" data-act="replay">Replay the incident</button>' : '<p class="do">Next step: ' + esc(w.action) + '</p>') + '</div>').join(''),
    unit: (id) => {
      const n = N[id], m = S.m[id]; let h = '';
      if (n.slots) {
        h += metric('Slots', S.used(id), m.held ? m.held + ' held back' : 'of ' + n.slots + ' in use', id + '.slots', n.slots);
        h += '<div class="cells">' + m.slots.map((s, i) => '<i class="' + (i >= n.slots - m.held ? 'held' : s ? 'busy' : '') + '"></i>').join('') + '</div>';
      }
      if (n.gate) { const j = m.job; const su = j ? S.suiteOf(j, S.now()) : null; h += metric('Suite', j ? su.index + '/' + su.total : 'idle', j ? su.name : 'no batch', id + '.slots', 1.3); }
      if (n.queue) h += metric('Queue', S.queues[n.queue], n.queue === 'review' ? 'shared review queue' : n.queue + ' queue', 'q.' + n.queue, 0);
      if (n.rate) h += metric('Requests', S.link[n.rate === 'api' ? 'webapi' : 'origin'].rate.toFixed(0), 'req/s', 'l.' + (n.rate === 'api' ? 'webapi' : 'origin'), 0);
      if (n.queues) h += metric('Queued', S.queues.coding + S.queues.review + S.queues.gate, 'code ' + S.queues.coding + ' · rev ' + S.queues.review + ' · gate ' + S.queues.gate, 'q.total', 0);
      h += metric('CPU', pct(m.cpu), 'of host', id + '.cpu', 0) + metric('Memory', gb(m.mem), '', id + '.mem', 0);
      return h;
    },
    tasks: (id) => {
      const m = S.m[id], run = m.slots.filter(Boolean);
      if (!run.length) return '<ul class="tasks"><li class="none">' + (m.held === N[id].slots ? 'No task can start until the dependency is there.' : 'No task running.') + '</li></ul>';
      return '<ul class="tasks">' + run.map((j) => { const el = Math.max(0, S.t - j.start); return '<li><span>task ' + j.id + '</span><span class="pb"><b style="width:' + clamp((el / j.dur) * 100, 2, 100).toFixed(0) + '%"></b></span><span>' + Math.floor(el / 60) + ':' + String(el % 60).padStart(2, '0') + '</span></li>'; }).join('') + '</ul>';
    },
    host: (id) => {
      const n = N[id], m = S.m[id]; let h = '';
      if (n.perfHost) {
        h += metric('CPU', pct(m.cpu), 'of ' + n.cores + (n.kind === 'vm' ? ' vCPU' : ' cores'), id + '.cpu', 1);
        h += '<div class="cores">' + m.cores.map((c) => '<i style="--v:' + c.toFixed(2) + '"></i>').join('') + '</div>';
        h += metric('Memory', gb(m.mem), 'of ' + n.memMax + ' GB', id + '.mem', n.memMax);
      } else if (n.hostCpu) h += metric('Host CPU', pct(m.cpu), 'mostly the VM', 'vm.cpu', 1);
      else if (n.kind === 'stack') h += metric('CPU', pct(m.cpu), 'of host', 'api.cpu', 0) + metric('Memory', gb(m.mem), '', 'api.mem', 0);
      else if (n.light) h += metric('Latency', Math.round(S.latency) + ' ms', 'to workstation', 'latency', 60, 'net');
      return h;
    },
    disk: (id) => {
      const n = N[id], m = S.m[id];
      return metric('Used', pct(m.frac), m.used.toFixed(0) + ' of ' + n.capacity + ' GB', id + '.used', n.capacity, 'sto') +
        '<div class="segbar">' + n.segs.map((s) => '<i class="' + (s.snapshot ? 'snap' : s.data ? 'data' : '') + '" style="width:' + ((s.gb / n.capacity) * 100).toFixed(2) + '%"></i>').join('') + '<u style="left:' + n.threshold * 100 + '%"></u></div>';
    },
    link: (id) => metric(id === 'overlay' ? 'Latency' : 'Traffic', rateText(id), id === 'overlay' ? S.link.overlay.rate.toFixed(0) + ' kB/s' : '', id === 'overlay' ? 'latency' : 'l.' + id, id === 'overlay' ? 60 : 0, 'net'),
    ext: (id) => (id === 'net' ? metric('Requests', S.link.pub.rate.toFixed(0), 'req/s', 'l.pub', 0, 'net') : metric('To origin', S.link.origin.rate.toFixed(0), 'req/s · cache hit ' + pct(S.cache), 'l.origin', 0, 'net')),
  };
  const TEXT = {
    rate: (id) => rateText(id),
    seg: (id) => { let v = ''; ['appdisk', 'vmdisk'].forEach((d) => N[d].segs.forEach((s) => { if (s.id === id) v = s.gb.toFixed(0) + ' GB'; })); return v; },
    used: (id) => pct(S.m[id].frac),
    ol: (id) => { const n = N[id], m = S.m[id]; if (n.slots) return S.used(id) + '/' + n.slots; if (n.gate) return m.job ? 'batch ' + m.job.batch : 'idle'; if (n.kind === 'disk') return pct(m.frac); if (n.perfHost || n.hostCpu) return 'cpu ' + pct(m.cpu); return ''; },
    olw: (id) => (S.warningsFor(id).some((w) => w.state === 'open') ? '!' : ''),
  };
  const live = (key, arg) => '<div data-live="' + key + (arg ? ':' + arg : '') + '"></div>';
  const bind = (key, arg) => '<span data-b="' + key + ':' + arg + '"></span>';

  function linkRows(ids, selfIds) {
    return '<ul class="rows">' + ids.map((id) => {
      const l = SV.linkById[id], peer = selfIds.indexOf(l.a) >= 0 ? l.b : l.a, out = selfIds.indexOf(l.a) >= 0;
      return '<li><button type="button" data-link="' + id + '"><svg viewBox="0 0 26 14" style="width:14px;height:8px;overflow:visible">' + glyph(l.kind).replace(/^<svg[^>]*>|<\/svg>$/g, '') + '</svg><span class="r-t"><strong>' + esc(l.name) + '</strong><span>' + (l.kind === 'overlay' ? 'peer: ' : out ? 'to ' : 'from ') + esc(N[peer].name) + '</span></span><span class="r-v">' + bind('rate', id) + '</span></button></li>';
    }).join('') + '</ul>';
  }
  function backupNote(n) {
    if (n.id === 'api') return ['The backup feature ships with the Studio API but is not configured: no encryption key and no target outside the app host.', 'Covered today: nothing. One manual snapshot exists on the same disk array.'];
    if (n.id === 'appdisk') return ['One manual snapshot (43 GB) sits on this same array. RAID 1 survives one failed disk; it does not protect against deletion, corruption or losing the host.', 'Project data and the workspace repository have no copy off this host.'];
    if (n.id === 'offsite') return ['Nothing is configured. Until a key and a target exist, a loss of the app host disk array is a loss of all project data.'];
    if (n.id === 'app' || n.id === 'install') return ['Backup is present as a feature and not configured. No key, no off-host target, one manual snapshot on the same disk.'];
    if (n.id === 'stack') return ['The Studio API in this stack carries the backup feature. It is not configured.'];
    if (n.parent === 'vm' || n.id === 'vm' || n.id === 'ws') return ['Review workspaces are rebuilt for every review. Nothing here needs a backup.'];
    if (n.volumes) return ['Works in the workspace repository, which has no copy off the app host.'];
    return null;
  }

  function renderPanel() {
    let h = '';
    if (st.selLink) {
      const l = SV.linkById[st.selLink];
      const crossed = l.cross.map((c) => (c.node.kind === 'boundary' ? 'system boundary' : c.node.name)).filter((v, i, a) => a.indexOf(v) === i);
      h += '<div class="p-head"><div class="p-kind">' + glyph(l.kind) + (l.kind === 'backup' ? 'Backup route' : 'Connection') + '</div><h2>' + esc(l.name) + '</h2><div class="p-path">' + esc(N[l.a].name) + (l.kind === 'overlay' ? ' and ' : ' to ') + esc(N[l.b].name) + '</div>' + closeBtn() + '</div>';
      h += '<div class="p-actions"><button type="button" class="btn primary" data-go="' + l.a + '">Go to ' + esc(N[l.a].name) + '</button><button type="button" class="btn" data-go="' + l.b + '">Go to ' + esc(N[l.b].name) + '</button></div>';
      if (l.kind !== 'backup') h += sec('Live', live('link', l.id), 'net', 'last 90 s');
      h += sec('Route', '<dl class="facts"><dt>Protocol</dt><dd>' + esc(l.proto) + '</dd><dt>Direction</dt><dd>' + esc(l.dir) + '</dd><dt>Crosses</dt><dd>' + (crossed.length ? esc(crossed.join(', ')) : 'no frame; both ends share a parent') + '</dd><dt>Boundary</dt><dd>' + (l.cross.some((c) => c.node.kind === 'boundary') ? 'Leaves the installation' : 'Stays inside the installation') + '</dd></dl>', l.kind === 'backup' ? 'backup' : 'net');
      if (l.kind === 'backup') h += sec('Backup', backupNote(N.offsite).map((t) => '<p class="note">' + esc(t) + '</p>').join(''), 'backup');
    } else {
      const id = st.sel || 'install', n = N[id];
      const path = SV.path(id).slice(0, -1).map((x) => (x.kind === 'boundary' ? 'Installation' : x.name));
      h += '<div class="p-head"><div class="p-kind">' + glyph(n.kind) + esc(SV.KIND[n.kind]) + '</div><h2>' + esc(n.name) + '</h2>' + (id === 'install' ? '' : '<div class="p-path">' + esc(path.length ? path.join(' / ') : 'Outside the installation') + ' · ' + esc(n.sub) + '</div>') + '<div class="p-state" data-live="state:' + id + '"></div>' + (id === 'install' ? '' : closeBtn()) + '</div>';
      if (id === 'install') {
        h += '<section class="sec"><div data-live="kpis" class="kpis"></div></section>';
        h += sec('Warnings', live('warnlist'), 'warn');
        h += sec('Trend', live('trend'), 'perf', 'last 90 s');
        h += sec('Activity', live('events'));
        h += sec('Outline', outline());
        h += sec('Legend', '<div class="legend-inline">' + legendHTML() + '</div><p class="keys">Drag to pan · scroll or pinch to zoom · double click to enter · 1 to 5 toggle layers · 0 fits · Esc goes up · Space pauses</p>');
      } else {
        h += '<div class="p-actions"><button type="button" class="btn primary" data-act="zoom">Zoom to</button>' + (n.parent ? '<button type="button" class="btn" data-sel="' + n.parent + '" data-fit="1">Up to ' + esc(N[n.parent].kind === 'boundary' ? 'installation' : N[n.parent].name) + '</button>' : '') + '</div>';
        if (S.warningsFor(id).length) h += '<section class="sec">' + live('warn', id) + '</section>';
        if (n.unit) { h += sec('Live', live('unit', id), 'perf', 'last 90 s'); if (n.slots) h += sec('Running tasks', live('tasks', id), 'perf'); }
        else if (n.kind === 'disk') h += sec('Live', live('disk', id), 'storage', 'last 90 s');
        else if (n.kind === 'external') h += sec('Live', live('ext', id), 'net', 'last 90 s');
        else if (n.perfHost || n.hostCpu || n.light || n.kind === 'stack') h += sec('Live', live('host', id), n.light ? 'net' : 'perf', 'last 90 s');
        if (n.children.length) {
          const kids = descendants(n).filter((c) => c.kind !== 'stack');
          h += sec('Runs here', '<ul class="outline">' + kids.map((c) => outlineRow(c, SV.path(c.id).length - SV.path(id).length - 1)).join('') + '</ul>');
        }
        const self = [id].concat(descendants(n).map((c) => c.id));
        const ls = SV.links.filter((l) => (self.indexOf(l.a) >= 0) !== (self.indexOf(l.b) >= 0) || (self.length === 1 && (l.a === id || l.b === id))).map((l) => l.id);
        if (ls.length) h += sec('Connections', linkRows(ls, self), 'net');
        if (n.kind === 'disk') h += sec('Volumes', '<ul class="rows">' + n.segs.map((s) => '<li><button type="button" tabindex="-1"><i class="sw" style="background:var(--c-' + (s.snapshot ? 'backup' : s.data ? 'storage' : 'ink3') + ')"></i><span class="r-t"><strong>' + esc(s.name) + '</strong><span>' + (s.snapshot ? 'only copy, on the same array' : s.ephemeral ? 'rebuilt per review' : s.data ? 'no off-host copy' : 'not user data') + '</span></span><span class="r-v">' + bind('seg', s.id) + '</span></button></li>').join('') + '</ul>', 'storage');
        if (n.volumes) h += sec('Storage', '<ul class="rows">' + n.volumes.map((v) => { const d = v === 'rw' ? 'vmdisk' : 'appdisk'; const s = N[d].segs.find((x) => x.id === v); return '<li><button type="button" data-sel="' + d + '"><i class="sw" style="background:var(--c-storage)"></i><span class="r-t"><strong>' + esc(s.name) + '</strong><span>on ' + esc(N[d].name) + ' · ' + esc(N[N[d].parent].name) + '</span></span><span class="r-v">' + bind('seg', v) + '</span></button></li>'; }).join('') + '</ul>', 'storage');
        const bn = backupNote(n);
        if (bn) h += sec('Backup', bn.map((t) => '<p class="note">' + esc(t) + '</p>').join(''), 'backup');
        if (n.facts) h += sec('Facts', '<dl class="facts">' + n.facts.map((kv) => '<dt>' + esc(kv[0]) + '</dt><dd>' + esc(kv[1]) + '</dd>').join('') + '</dl>');
      }
    }
    body.innerHTML = h; body.scrollTop = 0;
    liveCache.clear(); tickPanel();
  }
  const closeBtn = () => '<button type="button" class="p-close" data-act="close" aria-label="Close details"><svg viewBox="0 0 14 14"><path d="M2 2l10 10M12 2L2 12" stroke="currentColor" stroke-width="1.5" fill="none"/></svg></button>';
  function outlineRow(n, depth) { return '<li><button type="button" data-sel="' + n.id + '" style="padding-left:' + (4 + depth * 14) + 'px">' + glyph(n.kind) + '<span>' + esc(n.name) + '</span><em class="w" data-b="olw:' + n.id + '"></em><em data-b="ol:' + n.id + '"></em></button></li>'; }
  function outline() {
    const rows = [];
    (function walk(n, d) { n.children.forEach((c) => { rows.push(outlineRow(c, d)); walk(c, d + 1); }); })(N.install, 0);
    rows.push(outlineRow(N.cdn, 0), outlineRow(N.net, 0));
    return '<ul class="outline">' + rows.join('') + '</ul>';
  }
  const liveCache = new Map();
  function tickPanel() {
    body.querySelectorAll('[data-live]').forEach((el) => {
      const k = el.dataset.live, i = k.indexOf(':'), fn = LIVE[i < 0 ? k : k.slice(0, i)];
      const html = fn ? fn(i < 0 ? undefined : k.slice(i + 1)) : '';
      if (liveCache.get(el) !== html) { liveCache.set(el, html); el.innerHTML = html; }
    });
    body.querySelectorAll('[data-b]').forEach((el) => { const k = el.dataset.b, i = k.indexOf(':'); const v = String(TEXT[k.slice(0, i)](k.slice(i + 1))); if (el.textContent !== v) el.textContent = v; });
  }
  body.addEventListener('click', (e) => {
    const b = e.target.closest('button'); if (!b) return;
    if (b.dataset.act === 'close') { select('install', { stay: true }); if (phone) setSheet('peek'); }
    else if (b.dataset.act === 'zoom') { fitNode(st.sel); if (phone) setSheet('peek'); }
    else if (b.dataset.act === 'replay') S.replay();
    else if (b.dataset.link) selectLink(b.dataset.link);
    else if (b.dataset.go) { select(b.dataset.go, { stay: true }); fitNode(b.dataset.go); }
    else if (b.dataset.sel) { select(b.dataset.sel, { stay: !!b.dataset.fit }); if (b.dataset.fit) fitNode(b.dataset.sel); }
  });

  // phone sheet: tap or drag the handle
  const handle = $('#sheet-handle'); let hy = null;
  handle.addEventListener('pointerdown', (e) => { hy = e.clientY; handle.setPointerCapture(e.pointerId); });
  handle.addEventListener('pointerup', (e) => { if (hy == null) return; const d = e.clientY - hy; hy = null; if (Math.abs(d) < 8) setSheet(sheet === 'open' ? 'peek' : 'open'); else setSheet(d < 0 ? 'open' : 'peek'); });
  body.addEventListener('click', (e) => { if (phone && sheet === 'peek' && !e.target.closest('button')) setSheet('open'); });

  /* ================= boot ================= */
  function tick() {
    const d = $('.live-dot'); d.classList.remove('beat'); void d.offsetWidth; d.classList.add('beat');
    $('#clock').textContent = new Date().toLocaleTimeString('en-GB');
    const c = S.openWarnings().length, wc = $('#warn-count'); wc.textContent = c; wc.dataset.zero = String(c === 0);
    tickPanel();
  }
  let lastT = 0;
  function frame(t) {
    const dt = Math.min(0.05, (t - lastT) / 1000 || 0.016); lastT = t;
    stepCamera(t, dt);
    R.draw(t);
    if (!phone) R.keyplan(keyCv);
    crumbs();
    requestAnimationFrame(frame);
  }
  function boot() {
    S.start();
    const ff = parseInt(hp.get('ff') || '0', 10); if (ff > 0) S.forward(ff);
    loadWall = Date.now() - S.t * 1000;
    if (hp.has('layers')) { const on = hp.get('layers').split(','); Object.keys(st.layers).forEach((k) => (st.layers[k] = on.indexOf(k) >= 0)); }
    document.querySelectorAll('#layers button').forEach((b) => b.setAttribute('aria-pressed', String(!!st.layers[b.dataset.layer])));
    if (hp.get('pause') === '1') togglePause();
    R.init(cv); legend();
    measure(false); relayout(true);
    if (hp.get('sheet') === 'open' && phone) { sheet = 'open'; panel.dataset.sheet = 'open'; measure(false); }
    const sel = hp.get('sel'), link = hp.get('link'), view = hp.get('view');
    if (link && SV.linkById[link]) { st.selLink = link; st.sel = null; } else if (sel && N[sel]) st.sel = sel;
    if (phone && (st.sel !== 'install' || st.selLink) && hp.get('sheet') !== 'peek') { sheet = 'open'; panel.dataset.sheet = 'open'; measure(false); }
    renderPanel();
    if (view && N[view]) fitNode(view, true); else fitAll(true);
    S.onTick(tick); tick();
    new ResizeObserver(() => { const k = cam.k; measure(true); if (relayout(false)) fitAll(true); else cam.k = k; }).observe(stage);
    requestAnimationFrame(frame);
    if (document.fonts && document.fonts.ready) document.fonts.ready.then(() => R.palette());
  }
  boot();
  SV.api = { fitNode, fitAll, select, selectLink, toggleLayer };
})();
